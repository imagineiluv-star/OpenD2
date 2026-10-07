"""Build the pinned MPQ backend from source; no binary-only dependency downloads."""
import json
from pathlib import Path
import platform
import shutil
import subprocess

root = Path(__file__).resolve().parents[1]
lock = json.loads((root / "eng/dependencies.lock.json").read_text())["stormlib"]
work = root / ".local-tools/native"
source = work / "StormLib"
build = work / "build"
dist = work / "dist"
def run(*args):
    subprocess.run(args, check=True, cwd=root)
work.mkdir(parents=True, exist_ok=True)
if not source.exists():
    run("git", "clone", "--no-checkout", lock["repository"], str(source))
run("git", "-C", str(source), "checkout", "--detach", lock["commit"])
actual = subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
assert actual == lock["commit"]
assert not subprocess.check_output(["git", "-C", str(source), "status", "--porcelain"], text=True).strip(), "Native dependency has local changes"
args = ["cmake", "-S", str(root / "native"), "-B", str(build), "-DCMAKE_BUILD_TYPE=Release", "-DSTORMLIB_SOURCE=" + str(source)]
if platform.system() == "Darwin":
    args += ["-DCMAKE_OSX_ARCHITECTURES=arm64;x86_64"]
elif platform.system() == "Windows":
    args += ["-A", "x64"]
run(*args)
run("cmake", "--build", str(build), "--config", "Release", "--parallel", "2")
name = {"Windows": "opend2_mpq.dll", "Darwin": "libopend2_mpq.dylib", "Linux": "libopend2_mpq.so"}[platform.system()]
library = next(build.rglob(name))
dist.mkdir(exist_ok=True)
shutil.copy2(library, dist / name)
shutil.copy2(root / "THIRD_PARTY_NOTICES.md", dist / "THIRD_PARTY_NOTICES.md")
# Preserve the exact upstream source headers/notices, including bundled codecs.
notices = [source / "LICENSE", source / "src/zlib/zlib.h", source / "src/bzip2/bzlib.h", source / "src/pklib/pklib.h", source / "src/lzma/C/LzmaDec.h"]
with (dist / "StormLib-source-notices.txt").open("w", encoding="utf-8") as output:
    for file in notices:
        output.write("\n===== " + file.relative_to(source).as_posix() + " =====\n")
        output.write(file.read_text(encoding="utf-8", errors="replace"))
print(dist / name)
