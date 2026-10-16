"""Block Diablo II game data from release payloads (fails closed).

Scans a folder, a ZIP or a tar archive recursively, including archives inside archives and the file table of
Godot PCK files, and reports every entry that:
  * has a game-data extension (case-insensitive, also inside Godot import names such as foo.wav-<hash>.sample),
  * lives under a data/global/ path,
  * matches the SHA-256 of an original game file (optional list; never committed, see --original-sums), or
  * cannot be inspected (encrypted PCK directory, unreadable archive).
Files over --max-bytes are reported as warnings only.

  python eng/data_guard.py PATH... [--original-sums FILE] [--original-dir DIR]

The original hash list is only read at run time: from --original-sums / OPEND2_ORIGINAL_SHA256SUMS (a sha256sum-style
file such as the one beside your own install), or computed from --original-dir / D2_DATA_PATH. Exit code 1 on any block.
"""
import argparse
import hashlib
import io
import os
from pathlib import Path, PurePosixPath
import re
import struct
import sys
import tarfile
import zipfile

BLOCKED_EXTENSIONS = (".mpq", ".wav", ".mp3", ".ogg", ".bik", ".smk", ".dc6", ".dcc", ".dt1", ".ds1", ".cof",
                      ".pl2", ".tbl", ".dat", ".d2s")
# A blocked extension followed by an end or a Godot import suffix (".wav.import", ".wav-<md5>.sample").
EXTENSION_PATTERN = re.compile(r"\.(" + "|".join(e[1:] for e in BLOCKED_EXTENSIONS) + r")(?=$|[.\-])", re.I)
WARN_BYTES = 50 * 1024 * 1024
MAX_DEPTH = 8
PCK_ENCRYPTED_DIRECTORY = 1


def load_original_hashes(sums=None, folder=None):
    hashes = set()
    sums = sums or os.environ.get("OPEND2_ORIGINAL_SHA256SUMS")
    folder = folder or os.environ.get("D2_DATA_PATH")
    if sums:
        for line in Path(sums).read_text(encoding="utf-8").splitlines():
            match = re.match(r"([0-9a-fA-F]{64})\s", line)
            if match:
                hashes.add(match.group(1).lower())
    if folder:
        for path in sorted(Path(folder).rglob("*")):
            if path.is_file() and path.name != "SHA256SUMS":
                with path.open("rb") as stream:
                    hashes.add(hashlib.file_digest(stream, "sha256").hexdigest())
    return hashes


class Guard:
    def __init__(self, original_hashes=(), warn_bytes=WARN_BYTES):
        self.original = set(original_hashes)
        self.warn_bytes = warn_bytes
        self.blocked, self.warnings, self.scanned = [], [], 0

    def name_reasons(self, name):
        reasons = []
        lowered = name.replace("\\", "/").lower()
        parts = PurePosixPath(lowered).parts
        match = EXTENSION_PATTERN.search(parts[-1] if parts else lowered)
        if match:
            reasons.append(f"game-data extension .{match.group(1).lower()}")
        if "data/global/" in "/".join(parts) + "/":
            reasons.append("data/global path")
        return reasons

    def entry(self, label, data, depth=0):
        self.scanned += 1
        reasons = self.name_reasons(label.split("!")[-1])
        if self.original and hashlib.sha256(data).hexdigest() in self.original:
            reasons.append("matches an original game file hash")
        if len(data) > self.warn_bytes:
            self.warnings.append(f"{label}: {len(data)} bytes exceeds {self.warn_bytes}")
        if reasons:
            self.blocked.append(f"{label}: {', '.join(reasons)}")
        self.nested(label, data, depth)

    def nested(self, label, data, depth):
        if depth >= MAX_DEPTH:
            self.blocked.append(f"{label}: archive nesting deeper than {MAX_DEPTH}")
            return
        try:
            if data[:4] == b"GDPC":
                self.pck(label, data, depth + 1)
            elif data[:4] == b"PK\x03\x04" or data[:4] == b"PK\x05\x06":
                with zipfile.ZipFile(io.BytesIO(data)) as archive:
                    for info in archive.infolist():
                        if not info.is_dir():
                            self.entry(f"{label}!{info.filename}", archive.read(info), depth + 1)
            elif data[:2] == b"\x1f\x8b" or data[257:262] == b"ustar":
                with tarfile.open(fileobj=io.BytesIO(data)) as archive:
                    for member in archive.getmembers():
                        if member.isfile():
                            self.entry(f"{label}!{member.name}", archive.extractfile(member).read(), depth + 1)
                        elif not member.isdir():
                            self.entry(f"{label}!{member.name}", b"", depth + 1)
        except (zipfile.BadZipFile, tarfile.TarError, struct.error, EOFError, OSError, RuntimeError) as error:
            self.blocked.append(f"{label}: could not inspect archive ({error})")

    def pck(self, label, data, depth):
        version, _, _, _, flags = struct.unpack_from("<5I", data, 4)
        file_base, = struct.unpack_from("<Q", data, 24)
        if flags & PCK_ENCRYPTED_DIRECTORY:
            raise RuntimeError("encrypted PCK directory cannot be inspected")
        if version >= 3:
            directory, = struct.unpack_from("<Q", data, 32)
        elif version == 2:
            directory = 32 + 16 * 4
        else:
            raise RuntimeError(f"unsupported PCK format {version}")
        relative = bool(flags & 2) or version >= 3
        count, = struct.unpack_from("<I", data, directory)
        cursor = directory + 4
        for _ in range(count):
            length, = struct.unpack_from("<I", data, cursor)
            cursor += 4
            name = data[cursor:cursor + length].rstrip(b"\0").decode("utf-8", "replace")
            cursor += length
            offset, size = struct.unpack_from("<QQ", data, cursor)
            cursor += 16 + 16 + 4  # offset, size, md5, flags
            start = offset + (file_base if relative else 0)
            if start + size > len(data):
                raise RuntimeError(f"PCK entry {name} is out of bounds")
            self.entry(f"{label}!{name.removeprefix('res://')}", data[start:start + size], depth)

    def path(self, path, base=None):
        path = Path(path)
        if path.is_dir():
            for item in sorted(path.rglob("*")):
                if item.is_file() or item.is_symlink():
                    self.path(item, base or path)
            if not any(path.iterdir()):
                return
            return
        label = path.relative_to(base).as_posix() if base else path.name
        self.entry(label, path.read_bytes() if path.is_file() else b"")

    def check(self):
        if self.blocked:
            raise GameDataError(self.blocked)


class GameDataError(ValueError):
    def __init__(self, blocked):
        super().__init__("Game data found in release payload:\n  " + "\n  ".join(blocked))
        self.blocked = blocked


def scan(paths, original_hashes=()):
    guard = Guard(original_hashes)
    for path in paths:
        guard.path(path)
    return guard


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("paths", nargs="+", type=Path)
    parser.add_argument("--original-sums", type=Path)
    parser.add_argument("--original-dir", type=Path)
    args = parser.parse_args()
    guard = scan(args.paths, load_original_hashes(args.original_sums, args.original_dir))
    for warning in guard.warnings:
        print(f"WARNING {warning}")
    for item in guard.blocked:
        print(f"BLOCKED {item}", file=sys.stderr)
    if guard.blocked:
        print(f"DATA GUARD FAILED: {len(guard.blocked)} blocked of {guard.scanned} scanned", file=sys.stderr)
        sys.exit(1)
    print(f"DATA GUARD OK: {guard.scanned} entries, {len(guard.original)} original hashes")


if __name__ == "__main__":
    main()
