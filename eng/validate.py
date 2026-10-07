"""Build/test/import/smoke; optionally produce one desktop self-contained export."""
import argparse
import os
from pathlib import Path
import subprocess
import sys

os.environ.setdefault("DOTNET_PROCESSOR_COUNT", "2")
root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument("--godot", help="Pinned 4.6.3 .NET executable; default bootstrap output")
parser.add_argument("--export", choices=["Linux", "Windows", "macOS"])
args = parser.parse_args()
godot = args.godot or (root / ".local-tools/godot-path.txt").read_text(encoding="utf-8").strip()

def run(*command, capture=False):
    result = subprocess.run(command, cwd=root, text=True, stdout=subprocess.PIPE if capture else None, stderr=subprocess.STDOUT if capture else None)
    if capture:
        print(result.stdout)
    result.check_returncode()
    if capture and ("ERROR:" in result.stdout or "SCRIPT ERROR:" in result.stdout):
        raise SystemExit("Godot reported an error despite its exit code")
    return result.stdout

run(sys.executable, "eng/verify.py")
version = run(godot, "--version", capture=True)
assert version.strip().startswith("4.6.3.stable.mono."), version
run("dotnet", "restore", "OpenD2.sln", "--locked-mode", "-m:1", "-p:BuildInParallel=false")
run("dotnet", "build", "OpenD2.sln", "-c", "Debug", "--no-restore", "-m:1", "-p:BuildInParallel=false")
run("dotnet", "run", "--project", "tests/OpenD2.Tests", "-c", "Debug", "--no-build")
run(godot, "--headless", "--path", "src/OpenD2.Client", "--editor", "--import", "--quit", capture=True)
output = run(godot, "--headless", "--path", "src/OpenD2.Client", "--quit-after", "120", "--", "--smoke-test", capture=True)
assert "OPEND2_M0_READY" in output, "Startup marker missing"
assert "OPEND2_M104_PREVIEW_READY" in output, "DC6 preview marker missing"
assert "OPEND2_M105_ANIMATION_READY" in output, "DCC/COF animation marker missing"
assert "OPEND2_M106_MAP_READY" in output, "DT1/DS1 map marker missing"
assert "OPEND2_M107_TABLE_CACHE_READY" in output, "Map tables/cache marker missing"
assert "OPEND2_M201_SIMULATION_READY" in output, "Simulation/replay marker missing"
assert "OPEND2_M201_TICK_LOOP_READY" in output, "Live simulation tick loop marker missing"
if args.export:
    names = {"Linux": "OpenD2.x86_64", "Windows": "OpenD2.exe", "macOS": "OpenD2.zip"}
    destination = root / "artifacts" / args.export / names[args.export]
    destination.parent.mkdir(parents=True, exist_ok=True)
    run(godot, "--headless", "--path", "src/OpenD2.Client", "--export-release", args.export, str(destination), capture=True)
    assert destination.is_file(), destination
    print(f"EXPORT OK: {destination}")
