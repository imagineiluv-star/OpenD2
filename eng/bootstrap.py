"""Fetch pinned Godot .NET editor/templates and verify official SHA-512 digests."""
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[1]
lock = json.loads((ROOT / "eng/toolchain.json").read_text())
host = {"Linux": "linux", "Windows": "windows", "Darwin": "macos"}[platform.system()]
if host != "macos" and platform.machine().lower() not in ("x86_64", "amd64"):
    raise SystemExit("Linux/Windows bootstrap currently targets x64 only")
cache = ROOT / ".local-tools"
cache.mkdir(exist_ok=True)

def fetch(key):
    spec = lock["archives"][key]
    path = cache / spec["file"]
    if not path.exists():
        temporary = path.with_suffix(".download")
        urllib.request.urlretrieve(lock["baseUrl"] + spec["file"], temporary)
        temporary.replace(path)
    with path.open("rb") as stream:
        actual = hashlib.file_digest(stream, "sha512").hexdigest()
    if actual != spec["sha512"]:
        raise SystemExit(f"SHA512 mismatch: remove {path} and retry")
    return path

editor_dir = cache / "godot"
with zipfile.ZipFile(fetch(host)) as archive:
    archive.extractall(editor_dir)
# Preserve executable modes, including the macOS app's helper binaries.
    for info in archive.infolist():
        mode = info.external_attr >> 16
        if mode and not info.is_dir():
            (editor_dir / info.filename).chmod(mode & 0o777)
if host == "windows":
    data = Path(os.environ["APPDATA"])
elif host == "macos":
    data = Path.home() / "Library/Application Support"
else:
    data = Path(os.environ.get("XDG_DATA_HOME", str(Path.home() / ".local/share")))
templates = data / "Godot" if host != "linux" else data / "godot"
templates = templates / "export_templates" / lock["templateVersion"]
templates.mkdir(parents=True, exist_ok=True)
with zipfile.ZipFile(fetch("templates")) as archive:
    for info in archive.infolist():
        if info.is_dir():
            continue
        relative = Path(info.filename).relative_to("templates")
        destination = templates / relative
        destination.parent.mkdir(parents=True, exist_ok=True)
        with archive.open(info) as source, destination.open("wb") as target:
            shutil.copyfileobj(source, target)
if host == "windows":
    executable = next(editor_dir.rglob("*mono_win64_console.exe"))
elif host == "macos":
    executable = next(editor_dir.glob("*.app/Contents/MacOS/Godot"))
else:
    executable = next(editor_dir.rglob("*mono_linux.x86_64"))
(cache / "godot-path.txt").write_text(str(executable.resolve()), encoding="utf-8")
print(executable)
