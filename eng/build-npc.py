"""Build the pinned, CPU-only local NPC server. Model weights are never bundled."""
import json
from pathlib import Path
import platform
import shutil
import subprocess

root = Path(__file__).resolve().parents[1]
lock = json.loads((root / "eng/dependencies.lock.json").read_text(encoding="utf-8"))["llama_cpp"]
work = root / ".local-tools/llama"
source, build = work / "source", work / "build"
system = platform.system()
arch = {"AMD64": "x64", "x86_64": "x64", "arm64": "arm64", "aarch64": "arm64"}[platform.machine()]
rid = {"Windows": "win", "Linux": "linux", "Darwin": "osx"}[system] + "-" + arch
assert rid in ("win-x64", "linux-x64", "osx-arm64"), "NPC runtime currently targets x64 AVX2 Windows/Linux and Apple Silicon macOS"
dist = work / "dist" / rid
def run(*args):
    subprocess.run(args, cwd=root, check=True)
work.mkdir(parents=True, exist_ok=True)
if not source.exists():
    run("git", "clone", "--depth", "1", "--branch", lock["version"], lock["repository"], str(source))
actual = subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
assert actual == lock["commit"], "Unexpected llama.cpp revision; do not build mutable upstream code"
assert not subprocess.check_output(["git", "-C", str(source), "status", "--porcelain"], text=True).strip(), "NPC runtime source has local changes"
args = ["cmake", "-S", str(source), "-B", str(build), "-DCMAKE_BUILD_TYPE=Release", "-DBUILD_SHARED_LIBS=OFF",
        "-DGGML_NATIVE=OFF", "-DGGML_OPENMP=OFF", "-DGGML_METAL=OFF", "-DLLAMA_OPENSSL=OFF", "-DLLAMA_SUBPROCESS=OFF",
        "-DLLAMA_BUILD_TESTS=OFF", "-DLLAMA_BUILD_EXAMPLES=OFF", "-DLLAMA_BUILD_APP=OFF", "-DLLAMA_BUILD_UI=OFF",
        "-DLLAMA_USE_PREBUILT_UI=OFF", "-DLLAMA_BUILD_SERVER=ON"]
if arch == "x64":
    args += ["-DGGML_AVX=ON", "-DGGML_AVX2=ON", "-DGGML_AVX512=OFF", "-DGGML_FMA=ON", "-DGGML_F16C=ON"]
if system == "Windows":
    args += ["-A", "x64", "-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded"]
run(*args)
run("cmake", "--build", str(build), "--config", "Release", "--target", "llama-server", "--parallel", "2")
name = "llama-server.exe" if system == "Windows" else "llama-server"
binary = build / "bin" / ("Release" if system == "Windows" else "") / name
dist.mkdir(parents=True, exist_ok=True)
shutil.copy2(binary, dist / name)
shutil.copy2(source / "LICENSE", dist / "llama.cpp-LICENSE.txt")
notices = [source / "LICENSE", *sorted((source / "licenses").glob("*"))]
notices += sorted(p for p in (source / "vendor").rglob("*") if p.is_file() and p.name.startswith(("LICENSE", "COPYING", "NOTICE")))
(dist / "llama.cpp-dependency-notices.txt").write_text("\n\n".join(
    f"--- {p.relative_to(source)} ---\n{p.read_text(encoding='utf-8')}" for p in notices), encoding="utf-8")
shutil.copy2(root / "THIRD_PARTY_NOTICES.md", dist / "THIRD_PARTY_NOTICES.md")
shutil.copy2(root / "eng/notices/Qwen-Apache-2.0.txt", dist / "Qwen-Apache-2.0.txt")
(dist / "provenance.json").write_text(json.dumps({**lock, "rid": rid, "backend": "CPU", "x64_requirements": ["AVX2", "FMA", "F16C"] if arch == "x64" else []}, indent=2), encoding="utf-8")
run(str(dist / name), "--version")
print("NPC RUNTIME OK:", dist / name)
