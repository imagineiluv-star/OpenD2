"""Block Diablo II game data from release payloads (fails closed).

This guards the *release payload* (folders and archives about to be shipped), not the git history.
Scans a folder or a file recursively, including archives inside archives and the file table of Godot PCK files
(standalone or appended to an executable), and blocks every entry that:
  * has a game-data extension (case-insensitive, NFKC-normalised, trailing spaces/dots/~ ignored, also inside Godot
    import names such as foo.wav-<hash>.sample); .txt/.dat/.bin only under data/global|data/local or with a D2 table name,
  * lives under a data/global/ or data/local/ path,
  * starts with (or embeds at a 512-byte boundary) an MPQ header, so a renamed MPQ fails without a hash list,
  * matches the SHA-256 of an original game file (list read at run time; never committed, see --original-sums),
  * is a symlink, or
  * cannot be inspected: encrypted PCK directory or entry, unreadable archive, a compression/container format the
    guard cannot open (7z, rar, zstd, cab, lz4, ...), or an archive over the decompression size/ratio limits.
Files over --max-bytes are reported as warnings only.

  python eng/data_guard.py PATH... [--original-sums FILE] [--original-dir DIR] [--require-original-hashes]

The original hash list is only read at run time: from --original-sums / OPEND2_ORIGINAL_SHA256SUMS (a sha256sum-style
file such as the one beside your own install), or computed from --original-dir / D2_DATA_PATH. Exit code 1 on any
block. Without a hash list the result is reported as "PASS (no original hash list)", never as a bare PASS.
"""
import argparse
import bz2
import gzip
import hashlib
import io
import lzma
import os
from pathlib import Path, PurePosixPath
import re
import struct
import sys
import tarfile
import unicodedata
import zipfile

BLOCKED_EXTENSIONS = (".mpq", ".wav", ".mp3", ".ogg", ".bik", ".smk", ".dc6", ".dcc", ".dt1", ".ds1", ".cof",
                      ".pl2", ".tbl", ".d2s", ".pal", ".pcx", ".fnt")
# .txt, .dat and .bin are everywhere in third-party payloads (licenses, ICU's icudt72l.dat, runtime blobs), so they
# are only blocked
# under a D2 data path or when the file name is a known D2 table/palette name.
CONTEXT_EXTENSIONS = (".txt", ".dat", ".bin")
D2_TABLE_NAMES = frozenset("""
armor weapons misc levels lvlprest lvltypes lvlmaze lvlwarp lvlsub monstats monstats2 monlvl monprop monseq monsounds
montype monumod monai monequip monplace superuniques uniqueitems setitems sets skills skilldesc missiles itemstatcost
itemtypes treasureclassex treasureclass cubemain gems runes magicprefix magicsuffix rareprefix raresuffix automagic
uniqueprefix uniquesuffix properties states charstats experience difficultylevels npc objects objgroup objmode objtype
overlay pettype hireling inventory books belts sounds soundenviron storepage arena automap compcode composit qualityitems
lowqualityitems playerclass bodylocs elemtypes events hitclass monmode plrmode plrtype colors shrines pal colormap
""".split())
EXTENSION_PATTERN = re.compile(r"\.(" + "|".join(e[1:] for e in BLOCKED_EXTENSIONS) + r")(?=$|[.\-])", re.I)
CONTEXT_PATTERN = re.compile(r"\.(txt|dat|bin)(?=$|[.\-])", re.I)
UNINSPECTABLE_EXTENSIONS = (".7z", ".rar", ".zst", ".zstd", ".cab", ".lz4", ".lzma", ".lz", ".arj", ".ace", ".z",
                            ".sit", ".dmg", ".iso", ".wim", ".msi")
UNINSPECTABLE_MAGIC = {b"7z\xbc\xaf\x27\x1c": "7z", b"Rar!\x1a\x07": "rar", b"\x28\xb5\x2f\xfd": "zstd",
                       b"MSCF": "cab", b"\x04\x22\x4d\x18": "lz4", b"ISc(": "InstallShield cab"}
# lzma-alone ("]\0\0") and Unix compress ("\x1f\x9d") have 2-3 byte magics that hit ordinary files; those formats are
# caught by extension (.lzma, .z) instead.
MPQ_MAGIC = (b"MPQ\x1a", b"MPQ\x1b")
BIK_VERSIONS = b"bdfghik"
KB2_VERSIONS = b"abdfghij"
# A plausible install hash list has one entry per MPQ/executable (classic 1.11b: 7+ MPQs); fewer means a stub.
MIN_ORIGINAL_HASHES = 5
# Latin look-alikes that NFKC leaves alone (Cyrillic/Greek), folded only for matching extensions.
CONFUSABLES = str.maketrans("аеорсхмкнтвіјѕԁԛɡαεορτνκχ", "aeopcxmkhtbijsdqgaeoptvkx")
WARN_BYTES = 50 * 1024 * 1024
MAX_DEPTH = 8
MAX_TOTAL_BYTES = 4 * 1024 * 1024 * 1024  # decompressed bytes per scan
MAX_ENTRY_BYTES = 1024 * 1024 * 1024
MAX_RATIO = 200  # decompressed / compressed, for entries over RATIO_FLOOR
RATIO_FLOOR = 1024 * 1024
PCK_ENCRYPTED_DIRECTORY = 1
PCK_ENCRYPTED_FILE = 1


class Uninspectable(RuntimeError):
    pass


def load_original_hashes(sums=None, folder=None):
    """Explicit sums/folder arguments win; the environment is only consulted when neither is given."""
    hashes = set()
    if sums is None and folder is None:
        sums = os.environ.get("OPEND2_ORIGINAL_SHA256SUMS")
        folder = os.environ.get("D2_DATA_PATH")
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


def normalize_name(name):
    name = unicodedata.normalize("NFKC", name).replace("\\", "/").lower()
    return "/".join(part.rstrip(" .~_") for part in name.split("/"))


class Guard:
    def __init__(self, original_hashes=(), warn_bytes=WARN_BYTES, max_total_bytes=MAX_TOTAL_BYTES):
        self.original = set(original_hashes)
        self.warn_bytes = warn_bytes
        self.max_total_bytes = max_total_bytes
        self.expanded = 0
        self.blocked, self.warnings, self.scanned = [], [], 0
        self.context = {}

    @property
    def result(self):
        if self.blocked:
            return "FAIL"
        return "PASS" if self.original else "PASS (no original hash list)"

    def name_reasons(self, name):
        reasons = []
        lowered = normalize_name(name)
        parts = PurePosixPath(lowered).parts
        leaf = parts[-1] if parts else lowered
        joined = "/".join(parts) + "/"
        data_path = "data/global/" in joined or "data/local/" in joined
        folded = leaf.translate(CONFUSABLES)
        for pattern in (EXTENSION_PATTERN, CONTEXT_PATTERN):
            hit = pattern.search(folded)
            if hit and leaf[hit.start():hit.end()] != hit.group(0):  # the extension itself used look-alikes
                reasons.append("look-alike (non-Latin) characters in a game-data extension")
                break
        match = EXTENSION_PATTERN.search(leaf)
        if match:
            reasons.append(f"game-data extension .{match.group(1)}")
        context = CONTEXT_PATTERN.search(leaf)
        if context and (data_path or leaf[:context.start()] in D2_TABLE_NAMES):
            reasons.append(f"D2 table/palette .{context.group(1)}")
        if data_path:
            reasons.append("data/global path" if "data/global/" in joined else "data/local path")
        if leaf.endswith(UNINSPECTABLE_EXTENSIONS):
            reasons.append("compression/container format the guard cannot inspect")
        return reasons

    def account(self, label, size, compressed=None):
        if size > MAX_ENTRY_BYTES:
            raise Uninspectable(f"entry {label} expands to {size} bytes (limit {MAX_ENTRY_BYTES})")
        if compressed is not None and size > RATIO_FLOOR and size > MAX_RATIO * max(compressed, 1):
            raise Uninspectable(f"entry {label} compression ratio {size // max(compressed, 1)}:1 exceeds {MAX_RATIO}:1")
        self.expanded += size
        if self.expanded > self.max_total_bytes:
            raise Uninspectable(f"decompressed total exceeds {self.max_total_bytes} bytes")

    def entry(self, label, data, depth=0):
        self.scanned += 1
        reasons = self.name_reasons(self.context.pop(label, label).split("!")[-1])
        if self.original and hashlib.sha256(data).hexdigest() in self.original:
            reasons.append("matches an original game file hash")
        if self.mpq_offset(data) is not None:
            reasons.append("MPQ archive header")
        signature = self.media_signature(data)
        if signature:
            reasons.append(signature)
        if len(data) > self.warn_bytes:
            self.warnings.append(f"{label}: {len(data)} bytes exceeds {self.warn_bytes}")
        if reasons:
            self.blocked.append(f"{label}: {', '.join(reasons)}")
        self.nested(label, data, depth)

    @staticmethod
    def media_signature(data):
        if data[:4] == b"RIFF" and data[8:12] == b"WAVE":
            return "WAV audio (RIFF/WAVE header)"
        if (data[:3] == b"BIK" and data[3:4] and data[3:4] in BIK_VERSIONS) or \
                (data[:3] == b"KB2" and data[3:4] and data[3:4] in KB2_VERSIONS):
            return "Bink video header"
        return None

    @staticmethod
    def mpq_offset(data):
        # MPQs may be embedded (e.g. in an installer) at any 512-byte boundary.
        for magic in MPQ_MAGIC:
            index = data.find(magic)
            while index != -1:
                if index % 512 == 0:
                    return index
                index = data.find(magic, index + 1)
        return None

    def nested(self, label, data, depth):
        if depth >= MAX_DEPTH:
            self.blocked.append(f"{label}: archive nesting deeper than {MAX_DEPTH}")
            return
        try:
            for magic, kind in UNINSPECTABLE_MAGIC.items():
                if data.startswith(magic):
                    raise Uninspectable(f"{kind} data cannot be inspected")
            if data[:4] == b"GDPC":
                self.pck(label, data, depth + 1)
            elif data[:2] == b"\x1f\x8b" or data[:3] == b"BZh" or data[:6] == b"\xfd7zXZ\x00" or data[257:262] == b"ustar":
                self.tar_or_stream(label, data, depth + 1)
            elif zipfile.is_zipfile(io.BytesIO(data)):  # end-of-central-directory, so prefixed (SFX) zips count too
                with zipfile.ZipFile(io.BytesIO(data)) as archive:
                    for info in archive.infolist():
                        if info.is_dir():
                            continue
                        if info.flag_bits & 1:
                            raise Uninspectable(f"encrypted zip entry {info.filename}")
                        self.account(info.filename, info.file_size, info.compress_size)
                        self.entry(f"{label}!{info.filename}", archive.read(info), depth + 1)
            embedded = self.embedded_pck(data)
            if embedded is not None and data[:4] != b"GDPC":
                self.pck(f"{label}#embedded.pck", embedded, depth + 1)
        except (Uninspectable, zipfile.BadZipFile, zipfile.LargeZipFile, tarfile.TarError, struct.error, EOFError,
                OSError, RuntimeError, NotImplementedError, lzma.LZMAError, ValueError, MemoryError) as error:
            self.blocked.append(f"{label}: could not inspect archive ({error})")

    def tar_or_stream(self, label, data, depth):
        try:
            archive = tarfile.open(fileobj=io.BytesIO(data), mode="r:*")
        except tarfile.ReadError:
            archive = None
        if archive is not None:
            with archive:
                for member in archive.getmembers():
                    name = f"{label}!{member.name}"
                    if member.isfile():
                        self.account(member.name, member.size, len(data))
                        self.entry(name, archive.extractfile(member).read(), depth)
                    elif member.issym() or member.islnk():
                        self.entry(name, b"", depth)
                        self.blocked.append(f"{name}: link inside archive (-> {member.linkname})")
                    elif not member.isdir():
                        self.entry(name, b"", depth)
            return
        opener = {b"\x1f\x8b": lambda raw: gzip.GzipFile(fileobj=raw), b"BZ": bz2.BZ2File, b"\xfd7": lzma.LZMAFile}[data[:2]]
        out = bytearray()
        with opener(io.BytesIO(data)) as stream:
            while chunk := stream.read(1024 * 1024):
                out += chunk
                self.account(label, len(out), len(data))
        inner = re.sub(r"\.(gz|bz2|xz)$", "", label.split("!")[-1].split("/")[-1], flags=re.I) or "stream"
        self.entry(f"{label}!{inner}", bytes(out), depth)

    @staticmethod
    def embedded_pck(data):
        # Godot embed_pck: ... PCK ... <uint64 pck size> "GDPC" at the very end of the executable.
        if len(data) < 16 or data[-4:] != b"GDPC":
            return None
        size, = struct.unpack_from("<Q", data, len(data) - 12)
        start = len(data) - 12 - size
        if start < 0 or data[start:start + 4] != b"GDPC":
            raise Uninspectable("embedded PCK trailer does not point at a PCK header")
        return data[start:len(data) - 12]

    def pck(self, label, data, depth):
        version, _, _, _, flags = struct.unpack_from("<5I", data, 4)
        file_base, = struct.unpack_from("<Q", data, 24)
        if flags & PCK_ENCRYPTED_DIRECTORY:
            raise Uninspectable("encrypted PCK directory cannot be inspected")
        if version >= 3:
            directory, = struct.unpack_from("<Q", data, 32)
        elif version == 2:
            directory = 32 + 16 * 4
        else:
            raise Uninspectable(f"unsupported PCK format {version}")
        relative = bool(flags & 2) or version >= 3
        count, = struct.unpack_from("<I", data, directory)
        cursor = directory + 4
        for _ in range(count):
            length, = struct.unpack_from("<I", data, cursor)
            cursor += 4
            name = data[cursor:cursor + length].rstrip(b"\0").decode("utf-8", "replace")
            cursor += length
            offset, size = struct.unpack_from("<QQ", data, cursor)
            entry_flags, = struct.unpack_from("<I", data, cursor + 32)
            cursor += 16 + 16 + 4  # offset, size, md5, flags
            start = offset + (file_base if relative else 0)
            if start + size > len(data):
                raise Uninspectable(f"PCK entry {name} is out of bounds")
            if entry_flags & PCK_ENCRYPTED_FILE:
                self.blocked.append(f"{label}!{name.removeprefix('res://')}: encrypted PCK entry cannot be inspected")
                continue
            self.entry(f"{label}!{name.removeprefix('res://')}", data[start:start + size], depth)

    def path(self, path, base=None):
        path = Path(path)
        label = path.relative_to(base).as_posix() if base else path.name
        if base is not None and base.name:
            # Starting at pkg/data must still see data/global: name checks include the start folder's last parts.
            anchor = "/".join(Path(os.path.abspath(base)).parts[-2:])
            self.context[label] = f"{anchor}/{label}"
        if path.is_symlink():
            self.scanned += 1
            self.blocked.append(f"{label}: symlink in release payload")
            return
        if path.is_dir():
            for item in sorted(path.iterdir()):
                self.path(item, base or path)
            return
        self.entry(label, path.read_bytes())

    def check(self):
        if self.blocked:
            raise GameDataError(self.blocked)


class GameDataError(ValueError):
    def __init__(self, blocked):
        super().__init__("Game data found in release payload:\n  " + "\n  ".join(blocked))
        self.blocked = blocked


class MissingOriginalHashes(GameDataError):
    def __init__(self, found=0):
        problem = (f"only {found} original hashes (minimum {MIN_ORIGINAL_HASHES}); the list looks incomplete"
                   if found else "no original hash list")
        message = (f"{problem}. A tagged release requires the SHA-256 list of your own Diablo II install. Locally: "
                   "OPEND2_ORIGINAL_SHA256SUMS=<install>/SHA256SUMS or D2_DATA_PATH=<install>. In GitHub Actions: store "
                   "the sha256sum-style list as the repository secret OPEND2_ORIGINAL_SHA256SUMS (m0.yml writes it to a "
                   "temp file). For a dry run use --rehearsal / OPEND2_RELEASE_REHEARSAL=1.")
        ValueError.__init__(self, message)
        self.blocked = [message]


def scan(paths, original_hashes=(), require_original_hashes=False):
    if require_original_hashes and len(set(original_hashes)) < MIN_ORIGINAL_HASHES:
        raise MissingOriginalHashes(len(set(original_hashes)))
    guard = Guard(original_hashes)
    for path in paths:
        guard.path(path)
    return guard


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("paths", nargs="+", type=Path)
    parser.add_argument("--original-sums", type=Path)
    parser.add_argument("--original-dir", type=Path)
    parser.add_argument("--require-original-hashes", action="store_true")
    args = parser.parse_args()
    try:
        guard = scan(args.paths, load_original_hashes(args.original_sums, args.original_dir),
                     args.require_original_hashes)
    except MissingOriginalHashes as error:
        print(f"DATA GUARD FAILED: {error}", file=sys.stderr)
        sys.exit(1)
    for warning in guard.warnings:
        print(f"WARNING {warning}")
    for item in guard.blocked:
        print(f"BLOCKED {item}", file=sys.stderr)
    if guard.blocked:
        print(f"DATA GUARD FAILED: {len(guard.blocked)} blocked of {guard.scanned} scanned", file=sys.stderr)
        sys.exit(1)
    if not guard.original:
        print("WARNING no original hash list: renamed original files are only caught by name and MPQ header")
    print(f"DATA GUARD {guard.result}: {guard.scanned} entries, {len(guard.original)} original hashes")


if __name__ == "__main__":
    main()
