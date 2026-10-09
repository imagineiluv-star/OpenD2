"""Register user-supplied MPQs in a local vault. Never uploads or certifies game compatibility."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import tempfile

MAX_FILES = 256
MAX_FILE_BYTES = 8 * 1024**3
MAX_TOTAL_BYTES = 32 * 1024**3


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")


def dataset_id(identity):
    return hashlib.sha256(canonical(identity)).hexdigest()


def private_location(path):
    path = Path(path).expanduser().resolve()
    if any((parent / ".git").exists() for parent in (path, *path.parents)):
        raise ValueError("Choose a data vault outside a Git checkout.")
    return path


def names(directory):
    if not directory.is_dir():
        raise ValueError("Expected an existing MPQ directory.")
    files = []
    seen = set()
    for path in directory.iterdir():
        if path.suffix.lower() != ".mpq":
            continue
        name = path.name.lower()
        if not re.fullmatch(r"[a-z0-9][a-z0-9_.-]*\.mpq", name) or len(name) > 128:
            raise ValueError("Unsupported archive filename.")
        if name in seen or path.is_symlink() or not path.is_file():
            raise ValueError("Duplicate, linked or non-file MPQ entry.")
        seen.add(name)
        files.append(path)
        if len(files) > MAX_FILES:
            raise ValueError("Too many archives.")
    if not files:
        raise ValueError("No MPQ files supplied; no dataset was created.")
    return sorted(files, key=lambda p: p.name.lower())


def copy_hash(source, target=None):
    before = source.stat()
    if not 0 < before.st_size <= MAX_FILE_BYTES:
        raise ValueError("Empty archive or archive exceeds the size budget.")
    digest = hashlib.sha256()
    size = 0
    output = target.open("xb") if target is not None else None
    try:
        with source.open("rb") as stream:
            while block := stream.read(1024 * 1024):
                size += len(block)
                if size > MAX_FILE_BYTES:
                    raise ValueError("Archive exceeds the size budget.")
                digest.update(block)
                if output is not None:
                    output.write(block)
    finally:
        if output is not None:
            output.close()
    after = source.stat()
    if source.is_symlink() or (before.st_size, before.st_mtime_ns, before.st_ino) != (after.st_size, after.st_mtime_ns, after.st_ino) or size != before.st_size:
        raise ValueError("Archive changed during reading; retry with a stable source.")
    return {"name": source.name.lower(), "bytes": size, "sha256": digest.hexdigest()}


def verify(folder):
    folder = Path(folder)
    if folder.is_symlink():
        raise ValueError("Linked dataset directory is not supported.")
    manifest = folder / "manifest.json"
    if manifest.is_symlink() or manifest.stat().st_size > 128 * 1024:
        raise ValueError("Invalid manifest file.")
    data = json.loads(manifest.read_text(encoding="utf-8"))
    if set(data) != {"schema_version", "dataset_id", "identity", "compatibility_status", "gui_status"} or data["schema_version"] != 1:
        raise ValueError("Unsupported manifest schema.")
    identity = data["identity"]
    if not isinstance(identity, dict) or set(identity) != {"declared_version", "declared_language", "archives"}:
        raise ValueError("Invalid dataset identity.")
    for key in ("declared_version", "declared_language"):
        label(identity[key])
    if data["compatibility_status"] != "NOT_RUN" or data["gui_status"] != "NOT_RUN":
        raise ValueError("Registration manifest cannot certify compatibility or GUI QA.")
    if data["dataset_id"] != dataset_id(identity) or folder.name != data["dataset_id"]:
        raise ValueError("Dataset identity mismatch.")
    payload = folder / "mpq"
    if payload.is_symlink():
        raise ValueError("Linked payload directory is not supported.")
    actual = [copy_hash(path) for path in names(payload)]
    if len(list(payload.iterdir())) != len(actual) or {p.name for p in folder.iterdir()} != {"manifest.json", "mpq"}:
        raise ValueError("Unexpected files in dataset.")
    if sum(item["bytes"] for item in actual) > MAX_TOTAL_BYTES or actual != identity["archives"]:
        raise ValueError("Archive set, size or checksum mismatch.")
    return data


def label(value):
    if not isinstance(value, str) or not value.strip() or len(value) > 80 or any(ord(c) < 32 for c in value):
        raise ValueError("Version/language must be a nonempty label of at most 80 characters.")
    return value.strip()


def register(source, vault, version="unverified", language="unverified"):
    source = Path(source).expanduser().resolve()
    vault = private_location(vault)
    version, language = label(version), label(language)
    files = names(source)
    if vault == source or vault.is_relative_to(source) or source.is_relative_to(vault):
        raise ValueError("Source and vault must be separate directories.")
    if sum(p.stat().st_size for p in files) > MAX_TOTAL_BYTES:
        raise ValueError("Archive set exceeds the total size budget.")
    vault.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix=".register-", dir=vault) as temporary:
        stage = Path(temporary) / "dataset"
        payload = stage / "mpq"
        payload.mkdir(parents=True)
        archives = [copy_hash(path, payload / path.name.lower()) for path in files]
        if sum(item["bytes"] for item in archives) > MAX_TOTAL_BYTES:
            raise ValueError("Archive set exceeds the total size budget.")
        identity = {"declared_version": version, "declared_language": language, "archives": archives}
        key = dataset_id(identity)
        data = {"schema_version": 1, "dataset_id": key, "identity": identity,
                "compatibility_status": "NOT_RUN", "gui_status": "NOT_RUN"}
        (stage / "manifest.json").write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        checked = Path(temporary) / key
        stage.rename(checked)
        verify(checked)
        destination = vault / key
        if destination.exists() or destination.is_symlink():
            # A corrupted existing dataset is never silently repaired or overwritten.
            verify(destination)
        else:
            checked.rename(destination)
        return destination


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    add = commands.add_parser("register")
    add.add_argument("--source", required=True, type=Path)
    add.add_argument("--vault", required=True, type=Path)
    add.add_argument("--version", default="unverified")
    add.add_argument("--language", default="unverified")
    check = commands.add_parser("verify")
    check.add_argument("dataset", type=Path)
    args = parser.parse_args()
    if args.command == "register":
        folder = register(args.source, args.vault, args.version, args.language)
    else:
        folder = args.dataset
        verify(folder)
    print(f"DATA INTEGRITY OK: {folder}")
    print("MPQ decoding, game compatibility and GUI: NOT_RUN")


if __name__ == "__main__":
    main()
