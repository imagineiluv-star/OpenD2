"""Build/test/import/smoke; optionally produce one desktop self-contained export."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
from godot_process import run_godot

os.environ.setdefault("DOTNET_PROCESSOR_COUNT", "2")
root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument("--godot", help="Pinned .NET executable; default bootstrap output")
parser.add_argument("--export", choices=["Linux", "Windows", "macOS"])
args = parser.parse_args()
godot = args.godot or (root / ".local-tools/godot-path.txt").read_text(encoding="utf-8").strip()

def run(*command, capture=False, stage="godot"):
    if capture:
        return run_godot(command, cwd=root, log=root / "artifacts/validation" / f"{stage}.log")
    result = subprocess.run(command, cwd=root, text=True, encoding="utf-8")
    result.check_returncode()
    return result.stdout

run(sys.executable, "eng/verify.py")
run(sys.executable, "eng/check-state-vectors.py")
version = run(godot, "--version", capture=True, stage="version")
toolchain = json.loads((root / "eng/toolchain.json").read_text(encoding="utf-8"))
assert version.strip().startswith(toolchain["templateVersion"] + "."), version
run("dotnet", "restore", "OpenD2.sln", "--locked-mode", "-m:1", "-p:BuildInParallel=false")
run("dotnet", "build", "OpenD2.sln", "-c", "Debug", "--no-restore", "-m:1", "-p:BuildInParallel=false")
run("dotnet", "run", "--project", "tests/OpenD2.Tests", "-c", "Debug", "--no-build")
# 4.7.2 includes godotengine/godot#116548: no Android polling without its preset.
# --import waits for the scan to finish; no timing-based teardown workaround.
run(godot, "--headless", "--path", "src/OpenD2.Client", "--import", capture=True, stage="import")
output = run(godot, "--headless", "--path", "src/OpenD2.Client", "--quit-after", "600", "--max-fps", "60", "--", "--smoke-test", capture=True, stage="project-smoke")
assert "OPEND2_M0_READY" in output, "Startup marker missing"
assert "OPEND2_M104_PREVIEW_READY" in output, "DC6 preview marker missing"
assert "OPEND2_M105_ANIMATION_READY" in output, "DCC/COF animation marker missing"
assert "OPEND2_M106_MAP_READY" in output, "DT1/DS1 map marker missing"
assert "OPEND2_M107_TABLE_CACHE_READY" in output, "Map tables/cache marker missing"
assert "OPEND2_M201_SIMULATION_READY" in output, "Simulation/replay marker missing"
assert "OPEND2_M201_TICK_LOOP_READY" in output, "Live simulation tick loop marker missing"
assert "OPEND2_M202_COMBAT_READY" in output, "Combat/collision/replay marker missing"
assert "OPEND2_M203_WORLD_READY" in output, "Town/dungeon/quest/replay marker missing"
assert "OPEND2_M204_ITEMS_READY" in output, "Loot/inventory/equipment/replay marker missing"
assert "OPEND2_M205_SAVE_READY" in output, "Checkpoint/backup recovery marker missing"
assert "OPEND2_NPC01_DIALOGUE_READY" in output, "Asynchronous NPC dialogue marker missing"
assert "OPEND2_NPC02_RUNTIME_READY" in output, "Bundled NPC runtime execution marker missing"
assert "OPEND2_PLAY02_TERRAIN_READY" in output, "Legacy terrain/session integration marker missing"
assert "OPEND2_PLAY03_ACTOR_READY" in output, "Legacy actor texture/session integration marker missing"
assert "OPEND2_PLAY04_NAVIGATION_READY" in output, "Navigation integration marker missing"
assert "OPEND2_PLAY06_CONTINUOUS_READY" in output, "Continuous session marker missing"
assert "OPEND2_M206_HUD_READY" in output, "Player HUD/diagnostics marker missing"
assert "OPEND2_M206_AUDIO_READY" in output, "Scene audio controls/stream marker missing"
assert "OPEND2_M206_PANELS_READY" in output, "Inventory/equipment panel marker missing"
assert "OPEND2_M206_MENU_READY" in output, "Start menu/continue/checkpoint marker missing"
assert "OPEND2_PLAY08_SETUP_READY" in output, "Scene setup marker missing"
assert "OPEND2_PLAY09_ART_SETUP_READY" in output, "Actor artwork setup marker missing"
assert "OPEND2_PLAY10_NPC_READY" in output, "NPC artwork marker missing"
assert "OPEND2_PLAY11_HUD_READY" in output, "Legacy HUD artwork marker missing"
assert "OPEND2_PLAY12_ITEMS_READY" in output, "Legacy item artwork marker missing"
assert "OPEND2_PLAY12_ITEM_SETUP_READY" in output, "Item artwork setup marker missing"
assert "OPEND2_PLAY13_DEFINITIONS_READY" in output, "Item reference definitions marker missing"
assert "OPEND2_PLAY13_DEFINITION_SETUP_READY" in output, "Item definition setup marker missing"
assert "OPEND2_PLAY17_GROUND_READY" in output, "Ground item selection marker missing"
assert "OPEND2_PLAY16_POTIONS_READY" in output, "Potion/belt controls marker missing"
assert "OPEND2_PLAY15_SKILL_READY" in output, "Mana/skill controls marker missing"
assert "OPEND2_PLAY14_GRID_READY" in output, "Grid inventory marker missing"
assert "OPEND2_PLAY14_GRID_SETUP_READY" in output, "Grid setup marker missing"
if args.export:
    names = {"Linux": "OpenD2.x86_64", "Windows": "OpenD2.exe", "macOS": "OpenD2.zip"}
    destination = root / "artifacts" / args.export / names[args.export]
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.unlink(missing_ok=True)
    run(godot, "--headless", "--path", "src/OpenD2.Client", "--export-release", args.export, str(destination), capture=True, stage="export")
    assert destination.is_file(), destination
    print(f"EXPORT OK: {destination}")
    run(sys.executable, "eng/smoke-export.py", args.export)
