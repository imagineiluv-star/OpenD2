"""Move registered MPQ datasets between private local vaults; never uploads files."""
import argparse
import hashlib
import os
from pathlib import Path
import re
import stat
import tempfile
import zipfile

import original_data as data


def transfer_stream(source, target, limit):
    digest = hashlib.sha256()
    size = 0
    while block := source.read(1024 * 1024):
        size += len(block)
        if size > limit:
            raise ValueError("Transfer exceeds its declared size budget.")
        target.write(block)
        digest.update(block)
    return size, digest.hexdigest()


def pack(dataset, output):
    dataset = Path(dataset).absolute()
    manifest = data.verify(dataset)
    output = data.private_location(output)
    if output.is_relative_to(dataset.resolve()):
        raise ValueError("Transfer archive must be outside the dataset.")
    if output.exists():
        raise FileExistsError(output)
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix=".transfer-", dir=output.parent) as temporary:
        staged = Path(temporary) / "dataset.zip"
        with zipfile.ZipFile(staged, "x", compression=zipfile.ZIP_STORED, allowZip64=True) as archive:
            prefix = manifest["dataset_id"] + "/"
            # Snapshot the verified manifest, not a subsequently changed file.
            archive.writestr(prefix + "manifest.json", data.canonical(manifest))
            for entry in manifest["identity"]["archives"]:
                source = dataset / "mpq" / entry["name"]
                if source.is_symlink():
                    raise ValueError("Linked archive is not supported.")
                with source.open("rb") as input_stream, archive.open(prefix + "mpq/" + entry["name"], "w", force_zip64=True) as target:
                    size, digest = transfer_stream(input_stream, target, entry["bytes"])
                if (size, digest) != (entry["bytes"], entry["sha256"]):
                    raise ValueError("Archive changed while packing.")
        # Same filesystem hard link publishes a complete file without replacing an existing one.
        # Unsupported filesystems fail closed; no overwrite fallback.
        os.link(staged, output)
    return output


def unpack(archive_path, vault):
    vault = data.private_location(vault)
    with zipfile.ZipFile(archive_path) as archive:
        entries = archive.infolist()
        if not 2 <= len(entries) <= data.MAX_FILES + 1:
            raise ValueError("Invalid transfer entry count.")
        roots, seen = set(), set()
        total = 0
        for entry in entries:
            match = re.fullmatch(r"([0-9a-f]{64})/(manifest\.json|mpq/([a-z0-9][a-z0-9_.-]*\.mpq))", entry.filename)
            mode = stat.S_IFMT(entry.external_attr >> 16)
            if (not match or entry.filename in seen or mode not in (0, stat.S_IFREG)
                    or entry.flag_bits & 1 or entry.compress_type not in (zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED)):
                raise ValueError("Unsupported, duplicate or unsafe transfer entry.")
            roots.add(match[1]); seen.add(entry.filename)
            if match[3] and len(match[3]) > 128:
                raise ValueError("Archive filename exceeds budget.")
            limit = 128 * 1024 if match[2] == "manifest.json" else data.MAX_FILE_BYTES
            if not 0 < entry.file_size <= limit:
                raise ValueError("Transfer entry exceeds size budget.")
            total += entry.file_size
        if len(roots) != 1 or total > data.MAX_TOTAL_BYTES + 128 * 1024:
            raise ValueError("Invalid transfer root or total size.")
        key = roots.pop()
        if key + "/manifest.json" not in seen:
            raise ValueError("Missing transfer manifest.")
        vault.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix=".transfer-", dir=vault) as temporary:
            stage = Path(temporary) / key
            (stage / "mpq").mkdir(parents=True)
            for entry in entries:
                target = Path(temporary) / entry.filename
                with archive.open(entry) as source, target.open("xb") as output:
                    size, _ = transfer_stream(source, output, entry.file_size)
                if size != entry.file_size:
                    raise ValueError("Truncated transfer entry.")
            data.verify(stage)
            destination = vault / key
            if destination.exists() or destination.is_symlink():
                data.verify(destination)
            else:
                stage.rename(destination)
    return destination


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    export = commands.add_parser("pack")
    export.add_argument("dataset", type=Path)
    export.add_argument("--output", required=True, type=Path)
    restore = commands.add_parser("unpack")
    restore.add_argument("archive", type=Path)
    restore.add_argument("--vault", required=True, type=Path)
    args = parser.parse_args()
    result = pack(args.dataset, args.output) if args.command == "pack" else unpack(args.archive, args.vault)
    print(f"DATA TRANSFER INTEGRITY OK: {result}")
    print("MPQ decoding, game compatibility and GUI: NOT_RUN")


if __name__ == "__main__":
    main()
