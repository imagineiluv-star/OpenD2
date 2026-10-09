"""Run an exported package with SDK search paths removed; no model/network needed."""
import argparse
import os
from pathlib import Path
import plistlib
import subprocess
import tempfile

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument("preset", choices=["Linux", "Windows", "macOS"])
parser.add_argument("--folder", type=Path, help="Extracted release package to test")
args = parser.parse_args()
folder = (args.folder or root / "artifacts" / args.preset).resolve()
with tempfile.TemporaryDirectory(prefix="opend2-export-") as temporary:
    if args.preset == "macOS":
        subprocess.run(["ditto", "-x", "-k", str(folder / "OpenD2.zip"), temporary], check=True)
        apps = list(Path(temporary).glob("*.app"))
        assert len(apps) == 1, "Expected one exported application"
        info = plistlib.loads((apps[0] / "Contents/Info.plist").read_bytes())
        executable = apps[0] / "Contents/MacOS" / info["CFBundleExecutable"]
    else:
        executable = folder / ("OpenD2.exe" if args.preset == "Windows" else "OpenD2.x86_64")
    env = {k: v for k, v in os.environ.items() if not k.upper().startswith("DOTNET_ROOT") and k.upper() != "DOTNET_HOST_PATH"}
    env["PATH"] = os.pathsep.join([str(Path(os.environ["SystemRoot"]) / "System32"), os.environ["SystemRoot"]]) if args.preset == "Windows" else "/usr/bin:/bin:/usr/sbin:/sbin"
    env["DOTNET_MULTILEVEL_LOOKUP"] = "0"
    result = subprocess.run([str(executable), "--headless", "--quit-after", "600", "--max-fps", "60", "--", "--smoke-test"],
                            cwd=root, env=env, text=True, encoding="utf-8", stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=60)
    print(result.stdout)
    result.check_returncode()
    assert "ERROR:" not in result.stdout and "SCRIPT ERROR:" not in result.stdout, "Exported package reported an error"
    for marker in ("OPEND2_M206_MENU_READY", "OPEND2_PLAY08_SETUP_READY", "OPEND2_PLAY09_ART_SETUP_READY", "OPEND2_PLAY10_NPC_READY", "OPEND2_PLAY11_HUD_READY", "OPEND2_PLAY12_ITEMS_READY", "OPEND2_PLAY12_ITEM_SETUP_READY", "OPEND2_PLAY13_DEFINITIONS_READY", "OPEND2_PLAY13_DEFINITION_SETUP_READY", "OPEND2_PLAY16_POTIONS_READY", "OPEND2_PLAY15_SKILL_READY", "OPEND2_PLAY14_GRID_READY", "OPEND2_PLAY14_GRID_SETUP_READY", "OPEND2_M206_PANELS_READY", "OPEND2_M206_AUDIO_READY", "OPEND2_M206_HUD_READY", "OPEND2_PLAY06_CONTINUOUS_READY", "OPEND2_PLAY04_NAVIGATION_READY", "OPEND2_PLAY03_ACTOR_READY", "OPEND2_PLAY02_TERRAIN_READY", "OPEND2_M0_READY", "OPEND2_NPC01_DIALOGUE_READY", "OPEND2_NPC02_RUNTIME_READY", "OPEND2_M201_TICK_LOOP_READY"):
        assert marker in result.stdout, f"Exported package missing {marker}"
print("EXPORTED PACKAGE SMOKE OK:", args.preset)
