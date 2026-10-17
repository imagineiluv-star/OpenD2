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
import zlib

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
                       b"MSCF": "cab", b"\x04\x22\x4d\x18": "lz4", b"ISc(": "InstallShield cab", b"LZIP": "lzip"}
# lzma-alone ("]\0\0") and Unix compress ("\x1f\x9d") have 2-3 byte magics that hit ordinary files; those formats are
# caught by extension (.lzma, .z) instead.
MPQ_MAGIC = (b"MPQ\x1a", b"MPQ\x1b")
RSCC_DEFLATE, RSCC_GZIP = 1, 3  # Godot Compression::Mode values the guard can open (zlib/gzip wrappers)
DC6_TERMINATORS = (b"\xee\xee\xee\xee", b"\xcd\xcd\xcd\xcd")
DERIVED_MIN_BYTES = 64  # tiny cache files (empty/1-pixel) would match unrelated payload files
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


MPQ_MANIFEST = Path(__file__).resolve().parents[1] / "src" / "OpenD2.Assets" / "Manifests" / "d2-mpq-manifest.json"


class ManifestError(ValueError):
    """The committed MPQ manifest exists but cannot be read; reported without a traceback."""


def manifest_hashes(path=None):
    """SHA-256 of the supported MPQs from the committed d2-mpq-manifest.json (#108; hashes only, no game data)."""
    import json
    path = Path(path or MPQ_MANIFEST)
    if not path.is_file():
        return set()
    hashes = set()
    try:
        document = json.loads(path.read_text(encoding="utf-8"))
        versions = document.get("versions", []) if isinstance(document, dict) else None
        if not isinstance(versions, list) or not all(isinstance(v, dict) for v in versions):
            raise ValueError("expected {\"versions\": [ ... ]}")
    except (OSError, UnicodeDecodeError, ValueError) as error:
        raise ManifestError(f"cannot read the MPQ hash manifest {path}: {error}. Restore it from git "
                            f"(git checkout -- {path.name}) before packaging.") from None
    for version in versions:
        if version.get("placeholder"):
            continue
        for info in version.get("files", {}).values():
            digest = str(info.get("sha256", "")).lower()
            if re.fullmatch(r"[0-9a-f]{64}", digest):
                hashes.add(digest)
    return hashes


def load_original_hashes(sums=None, folder=None):
    """Explicit sums/folder arguments win. Otherwise: the committed MPQ manifest (default, so no secret is needed),
    plus OPEND2_ORIGINAL_SHA256SUMS / D2_DATA_PATH when set."""
    hashes = set()
    if sums is None and folder is None:
        hashes |= manifest_hashes()
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


CACHE_VARIABLE = "D2_ASSET_CACHE"  # the app's name (src/OpenD2.Assets/D2DataPaths.cs)
DEPRECATED_CACHE_VARIABLE = "OPEND2_ASSET_CACHE"


def app_cache_root():
    """The app's default cache: .NET LocalApplicationData/OpenD2/asset-cache."""
    if sys.platform == "win32":
        base = Path(os.environ.get("LOCALAPPDATA") or Path.home() / "AppData" / "Local")
    elif sys.platform == "darwin":
        base = Path.home() / "Library" / "Application Support"
    else:
        base = Path(os.environ.get("XDG_DATA_HOME") or Path.home() / ".local" / "share")
    return base / "OpenD2" / "asset-cache"


def cache_roots(cache=None):
    """Explicit argument, then D2_ASSET_CACHE, then the deprecated OPEND2_ASSET_CACHE; otherwise the app default plus the
    legacy ~/.cache/opend2/asset-cache. "none" disables (guard only)."""
    setting = cache
    if setting is None:
        setting = os.environ.get(CACHE_VARIABLE)
    if setting is None and os.environ.get(DEPRECATED_CACHE_VARIABLE) is not None:
        setting = os.environ[DEPRECATED_CACHE_VARIABLE]
        print(f"WARNING {DEPRECATED_CACHE_VARIABLE} is deprecated; use {CACHE_VARIABLE}", file=sys.stderr)
    if setting == "none":
        return []
    if setting:
        return [Path(setting)]
    legacy = Path(os.environ.get("XDG_CACHE_HOME") or Path.home() / ".cache") / "opend2" / "asset-cache"
    return [app_cache_root(), legacy]


def load_derived_hashes(cache=None):
    """SHA-256 of files OpenD2 extracted into its asset cache (#108), read at run time and never committed.

    Roots come from cache_roots() (D2_ASSET_CACHE like the app; D2_ASSET_CACHE=none disables). Only folders with
    a cache-manifest.json written by the p1-mpq converter count. Converted derivatives (PNG of a DC6, etc.) are hashed
    as they sit in the cache; anything re-encoded afterwards cannot be recognised (documented residual risk).
    """
    hashes = set()
    for root in cache_roots(cache):
        if root.is_dir():
            hashes |= _cache_hashes(root)
    return hashes


def _cache_hashes(root):
    hashes = set()
    for manifest in sorted(root.rglob("cache-manifest.json")):
        try:
            import json
            if not str(json.loads(manifest.read_text(encoding="utf-8")).get("converter", "")).startswith("p1-mpq"):
                continue
        except (OSError, ValueError):
            continue
        for path in sorted(manifest.parent.rglob("*")):
            if path.is_file() and not path.is_symlink() and path != manifest and path.stat().st_size >= DERIVED_MIN_BYTES:
                with path.open("rb") as stream:
                    hashes.add(hashlib.file_digest(stream, "sha256").hexdigest())
    return hashes


def normalize_name(name):
    name = unicodedata.normalize("NFKC", name).replace("\\", "/").lower()
    return "/".join(part.rstrip(" .~_") for part in name.split("/"))


class Guard:
    def __init__(self, original_hashes=(), warn_bytes=WARN_BYTES, max_total_bytes=MAX_TOTAL_BYTES, derived_hashes=()):
        self.original = set(original_hashes)
        self.derived = set(derived_hashes)
        self.warn_bytes = warn_bytes
        self.max_total_bytes = max_total_bytes
        self.expanded = 0
        self.blocked, self.warnings, self.scanned = [], [], 0
        self.context = {}
        self.unknown = []

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
        data_path = bool(re.search(r"(?:^|/)data[/_\-. ]?(global|local)[/_\-. ]", joined))
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
            reasons.append("data/global path" if re.search(r"(?:^|/)data[/_\-. ]?global", joined) else "data/local path")
        if leaf.endswith(UNINSPECTABLE_EXTENSIONS):
            reasons.append("compression/container format the guard cannot inspect")
        return reasons

    def account(self, label, size, compressed=None, commit=True):
        if size > MAX_ENTRY_BYTES:
            raise Uninspectable(f"entry {label} expands to {size} bytes (limit {MAX_ENTRY_BYTES})")
        if compressed is not None and size > RATIO_FLOOR and size > MAX_RATIO * max(compressed, 1):
            raise Uninspectable(f"entry {label} compression ratio {size // max(compressed, 1)}:1 exceeds {MAX_RATIO}:1")
        if self.expanded + size > self.max_total_bytes:
            raise Uninspectable(f"decompressed total exceeds {self.max_total_bytes} bytes")
        if commit:
            self.expanded += size

    def entry(self, label, data, depth=0):
        self.scanned += 1
        reasons = self.name_reasons(self.context.pop(label, label).split("!")[-1])
        if self.original or self.derived:
            digest = hashlib.sha256(data).hexdigest()
            if digest in self.original:
                reasons.append("matches an original game file hash")
            if digest in self.derived:
                reasons.append("matches an extracted asset-cache file hash")
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
        if data[:4] == b"OggS" and data[4:5] == b"\0":
            return "Ogg audio (OggS page)"
        if Guard.looks_like_dc6(data):
            return "DC6 sprite header"
        return None

    @staticmethod
    def looks_like_dc6(data):
        # version 6, encoding 0, terminator EEEEEEEE/CDCDCDCD, 1-32 directions x 1-256 frames, and the first frame
        # pointer lands right after the pointer table: five independent constraints, so false positives are negligible.
        if len(data) < 28 or data[12:16] not in DC6_TERMINATORS:
            return False
        version, _flags, encoding, directions, frames = struct.unpack_from("<3I4xII", data, 0)
        if version != 6 or encoding != 0 or not (1 <= directions <= 32 and 1 <= frames <= 256):
            return False
        table_end = 24 + 4 * directions * frames
        return len(data) >= table_end + 4 and struct.unpack_from("<I", data, 24)[0] == table_end

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
            elif data[:4] == b"RSCC":
                self.rscc(label, data, depth + 1)
            elif data[:2] == b"\x1f\x8b" or data[:3] == b"BZh" or data[:6] == b"\xfd7zXZ\x00" or data[257:262] == b"ustar":
                self.tar_or_stream(label, data, depth + 1)
            elif zipfile.is_zipfile(io.BytesIO(data)):  # end-of-central-directory, so prefixed (SFX) zips count too
                with zipfile.ZipFile(io.BytesIO(data)) as archive:
                    for info in archive.infolist():
                        if info.is_dir():
                            continue
                        if info.flag_bits & 1:
                            raise Uninspectable(f"encrypted zip entry {info.filename}")
                        if (info.external_attr >> 16) & 0o170000 == 0o120000:
                            self.scanned += 1
                            self.blocked.append(f"{label}!{info.filename}: symlink entry inside zip")
                            continue
                        self.account(info.filename, info.file_size, info.compress_size)
                        self.entry(f"{label}!{info.filename}", archive.read(info), depth + 1)
            elif len(data) > 2 and data[0] & 0x0f == 8 and data[0] >> 4 <= 7 and ((data[0] << 8) | data[1]) % 31 == 0:
                self.zlib_stream(label, data, depth + 1)  # RFC 1950, any window size (CMF 0x08..0x78)
            elif not self.raw_deflate(label, data, depth + 1):
                self.note_unknown(label, data)
            embedded = self.embedded_pck(data)
            if embedded is not None and data[:4] != b"GDPC":
                self.pck(f"{label}#embedded.pck", embedded, depth + 1)
            elif data[:4] != b"GDPC":
                self.inner_pcks(label, data, depth + 1)
        except (Uninspectable, zipfile.BadZipFile, zipfile.LargeZipFile, tarfile.TarError, struct.error, EOFError,
                OSError, RuntimeError, NotImplementedError, lzma.LZMAError, zlib.error, ValueError, MemoryError) as error:
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
                self.account(label, len(out), len(data), commit=False)
        self.account(label, len(out), len(data))
        inner = re.sub(r"\.(gz|bz2|xz)$", "", label.split("!")[-1].split("/")[-1], flags=re.I) or "stream"
        self.entry(f"{label}!{inner}", bytes(out), depth)

    KNOWN_MAGIC = (b"\x7fELF", b"MZ", b"\xcf\xfa\xed\xfe", b"\xce\xfa\xed\xfe", b"\xfe\xed\xfa", b"\xca\xfe\xba\xbe",
                   b"\x89PNG", b"RSRC", b"ECFG", b"GST2", b"GDST", b"RIFF", b"\xff\xd8\xff", b"GIF8", b"BSJB", b"wOF")

    def note_unknown(self, label, data):
        """Binary entries of no recognised format are not blocked (Godot/.NET ship many) but listed for review."""
        if data and b"\0" in data[:512] and not data.startswith(self.KNOWN_MAGIC) and len(self.unknown) < 500:
            self.unknown.append(label)

    def raw_deflate(self, label, data, depth):
        """A file that is one complete raw DEFLATE stream (no header) is inflated and rescanned. Ordinary binaries
        fail within the first bytes or stop long before their end, so they are left alone."""
        if len(data) < 64:
            return False
        inflater, out = zlib.decompressobj(-15), bytearray()
        try:
            out += inflater.decompress(data[:4096], 1 << 20)
            if inflater.unconsumed_tail or (inflater.eof and len(inflater.unused_data) > 16
                                            and not self.substantial(data, inflater, out)):
                return False
            for offset in range(4096, len(data), 65536):
                if inflater.eof:
                    break
                out += inflater.decompress(data[offset:offset + 65536])
                self.account(label, len(out), len(data), commit=False)
            out += inflater.flush()
        except zlib.error:
            return False
        if not inflater.eof or not out:
            return False
        trailer = inflater.unused_data
        if len(trailer) > 16 and not self.substantial(data, inflater, out):
            return False
        self.account(label, len(out), len(data))
        self.entry(f"{label}!deflate", bytes(out), depth)
        if trailer:
            self.entry(f"{label}!deflate-trailer", trailer, depth)
        return True

    @staticmethod
    def substantial(data, inflater, out):
        """A raw DEFLATE stream followed by other bytes only counts when the stream itself is long (>= 1 KiB in and
        out, allowing stored blocks); short accidental "streams" at the start of ordinary binaries must not trigger rescans."""
        consumed = len(data) - len(inflater.unused_data)
        return consumed >= 1024 and len(out) * 10 >= consumed * 9

    def inner_pcks(self, label, data, depth):
        """A PCK at an arbitrary offset (not Godot's trailer layout): parse every plausible GDPC header found."""
        index, tried = data.find(b"GDPC", 1), 0
        while index != -1 and tried < 16:
            if len(data) >= index + 40 and struct.unpack_from("<I", data, index + 4)[0] in (2, 3):
                tried += 1
                before = len(self.blocked)
                try:
                    self.pck(f"{label}@{index}.pck", data[index:], depth)
                except (Uninspectable, struct.error):
                    del self.blocked[before:]  # not a real PCK header
            index = data.find(b"GDPC", index + 1)

    def zlib_stream(self, label, data, depth):
        """Raw zlib (RFC 1950) payloads are opened; a valid-looking header that doesn't inflate is just data."""
        inflater, out = zlib.decompressobj(), bytearray()
        for offset in range(0, len(data), 65536):
            try:
                out += inflater.decompress(data[offset:offset + 65536])
            except zlib.error as error:
                if offset == 0:
                    return
                raise Uninspectable(f"zlib stream does not inflate ({error})")
            self.account(label, len(out), len(data), commit=False)
        out += inflater.flush()
        if not inflater.eof:
            raise Uninspectable("zlib stream is truncated")
        self.account(label, len(out), len(data))
        self.entry(f"{label}!zlib", bytes(out), depth)
        if inflater.unused_data:  # bytes after the stream ship too: scan them like any other entry
            self.entry(f"{label}!zlib-trailer", inflater.unused_data, depth)

    def rscc(self, label, data, depth):
        """Godot compressed resource (FileAccessCompressed): deflate/gzip blocks are inflated and rescanned;
        zstd/FastLZ/brotli blocks cannot be opened with the standard library, so they block (fail closed)."""
        mode, block_size = struct.unpack_from("<II", data, 4)
        if mode not in (RSCC_DEFLATE, RSCC_GZIP):
            raise Uninspectable(f"Godot RSCC resource with compression mode {mode} cannot be inspected")
        for total_format, header in (("<I", 16), ("<Q", 20)):
            total, = struct.unpack_from(total_format, data, 12)
            if block_size == 0:
                break
            blocks = total // block_size + 1
            table_end = header + 4 * blocks
            if table_end > len(data):
                continue
            sizes = struct.unpack_from(f"<{blocks}I", data, header)
            if table_end + sum(sizes) != len(data):
                continue
            out, cursor = bytearray(), table_end
            for size in sizes:
                # Each block inflates to at most block_size bytes; anything more is a bomb or hidden payload.
                inflater = zlib.decompressobj(15 if mode == RSCC_DEFLATE else 31)
                chunk = inflater.decompress(data[cursor:cursor + size], block_size + 1)
                if len(chunk) > block_size or inflater.unconsumed_tail:
                    raise Uninspectable(f"Godot RSCC block inflates beyond its {block_size}-byte block size")
                chunk += inflater.flush(block_size + 1 - len(chunk))
                if len(chunk) > block_size or not inflater.eof:
                    raise Uninspectable("Godot RSCC block is truncated or oversized")
                out += chunk
                self.account(label, len(out), len(data), commit=False)
                cursor += size
            if len(out) != total:
                raise Uninspectable(f"Godot RSCC inflates to {len(out)} bytes, header declares {total}")
            self.account(label, len(out), len(data))
            self.entry(f"{label}!rscc", bytes(out), depth)
            return
        raise Uninspectable("Godot RSCC block table is inconsistent")

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


def scan(paths, original_hashes=(), require_original_hashes=False, derived_hashes=()):
    if require_original_hashes and len(set(original_hashes)) < MIN_ORIGINAL_HASHES:
        raise MissingOriginalHashes(len(set(original_hashes)))
    guard = Guard(original_hashes, derived_hashes=derived_hashes)
    for path in paths:
        guard.path(path)
    return guard


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("paths", nargs="+", type=Path)
    parser.add_argument("--original-sums", type=Path)
    parser.add_argument("--original-dir", type=Path)
    parser.add_argument("--require-original-hashes", action="store_true")
    parser.add_argument("--asset-cache", help="extracted asset cache to add to the blocklist ('none' disables; "
                                              "default D2_ASSET_CACHE or the app's cache folder)")
    args = parser.parse_args()
    try:
        guard = scan(args.paths, load_original_hashes(args.original_sums, args.original_dir),
                     args.require_original_hashes, load_derived_hashes(args.asset_cache))
    except ManifestError as error:
        print(f"DATA GUARD FAILED: {error}", file=sys.stderr)
        sys.exit(1)
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
    if guard.unknown:
        print(f"NOTE {len(guard.unknown)} binary entries of unknown format (not blocked): " + ", ".join(guard.unknown[:20]))
    if not guard.original:
        print("WARNING no original hash list: renamed original files are only caught by name and MPQ header")
    print(f"DATA GUARD {guard.result}: {guard.scanned} entries, {len(guard.original)} original hashes, "
          f"{len(guard.derived)} asset-cache hashes")


if __name__ == "__main__":
    main()
