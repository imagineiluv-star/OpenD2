"""Package validated exports and produce a reviewable, explicitly untested GUI QA handoff."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
import zipfile

ROOT = Path(__file__).resolve().parents[1]
PLATFORMS = {"Linux": "linux-x64.tar.gz", "Windows": "windows-x64.zip", "macOS": "macos-universal.zip"}
CASES = [
    ("QA-01", "Clean install and visible startup without SDK or model", True),
    ("QA-02", "Move, accept quest, enter Cellar, fight and return to Guide", True),
    ("QA-03", "Pick up, equip, unequip and drop an item", True),
    ("QA-04", "Save, exit, relaunch and load the same checkpoint", True),
    ("QA-05", "Basic NPC dialogue and explicit quest confirmation", True),
    ("QA-06", "HUD, saved display/audio settings, audible effects, input focus and Korean text", True),
    ("QA-07", "Optional model download/cancel/resume/run/fallback/remove", False),
    ("QA-08", "Play across a ten-minute replay rollover, verify, save and reload", True),
    ("QA-09", "Owned legacy scene: terrain, animation, audio mapping, quest loop and reload", False),
    ("QA-10", "Separately scheduled two-hour visible gameplay soak", False),
    ("QA-11", "Grid inventory: drag, keyboard placement, cancellation and checkpoint", True),
    ("QA-12", "Power strike: mana, cooldown, input focus and checkpoint", True),
    ("QA-13", "Potions: bag/belt transfers, one use per key press and checkpoint", True),
    ("QA-14", "Overlapping ground names: selected approach/pickup and cancellation", True),
]


def check_version(version, allow_ci=False):
    if (allow_ci and version == "ci") or re.fullmatch(r"v[0-9]+\.[0-9]+\.[0-9]+-rc\.[1-9][0-9]*", version):
        return version
    raise ValueError("Expected a release candidate such as v0.2.0-rc.1")


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def write_json(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def package_name(version, preset):
    return f"OpenD2-{version}-{PLATFORMS[preset]}"


def mpq_kit(output, version, commit):
    # Only authored configuration, metadata and instructions; never game data.
    path = output / "mpq-qa-kit.zip"
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as archive:
        for name in ("mpq_handoff.py", "demo-mpq-hashes.json", "demo-town-scene.json", "MPQ_GROK_TASK.md"):
            archive.writestr(zipfile.ZipInfo(name, (2026, 1, 1, 0, 0, 0)), (ROOT / "eng/qa" / name).read_bytes())
        archive.writestr(zipfile.ZipInfo("kit.json", (2026, 1, 1, 0, 0, 0)),
                         json.dumps({"schema_version": 1, "version": version, "commit": commit}))
    return path


def pack(source, output, preset, version, commit, smoke=False):
    check_version(version, allow_ci=True)
    if not re.fullmatch(r"[0-9a-f]{40}", commit):
        raise ValueError("A full source commit SHA is required")
    source, output = source.resolve(), output.resolve()
    if output == source or output.is_relative_to(source):
        raise ValueError("Package output must be outside the export directory")
    output.mkdir(parents=True, exist_ok=True)
    archive = output / package_name(version, preset)
    if archive.exists():
        raise FileExistsError(archive)
    if preset == "macOS":
        # Keep Godot's app bundle permissions, links and resource metadata intact.
        with zipfile.ZipFile(source / "OpenD2.zip") as app:
            if not any(".app/Contents/Info.plist" in item for item in app.namelist()):
                raise ValueError("macOS export has no application bundle")
        shutil.copyfile(source / "OpenD2.zip", archive)
    else:
        executable = source / ("OpenD2.x86_64" if preset == "Linux" else "OpenD2.exe")
        if not executable.is_file() or not list(source.glob("*.pck")):
            raise ValueError("Export must include its executable and PCK")
        if preset == "Linux" and not executable.stat().st_mode & 0o111:
            raise ValueError("Linux executable has no execute permission")
        with tempfile.TemporaryDirectory(prefix="opend2-package-") as temporary:
            stage = Path(temporary) / "payload"
            shutil.copytree(source, stage, symlinks=True)
            for name in ("LICENSE", "THIRD_PARTY_NOTICES.md"):
                shutil.copyfile(ROOT / name, stage / name)
            if preset == "Linux":
                with tarfile.open(archive, "w:gz") as target:
                    for item in sorted(stage.iterdir()):
                        target.add(item, arcname=item.name)
            else:
                with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as target:
                    for item in sorted(stage.rglob("*")):
                        target.write(item, item.relative_to(stage).as_posix())
    if smoke:
        with tempfile.TemporaryDirectory(prefix="opend2-packaged-smoke-") as temporary:
            extracted = Path(temporary)
            if preset == "Linux":
                with tarfile.open(archive) as target:
                    target.extractall(extracted, filter="data")
            elif preset == "Windows":
                with zipfile.ZipFile(archive) as target:
                    target.extractall(extracted)
            else:
                shutil.copyfile(archive, extracted / "OpenD2.zip")
            subprocess.run([sys.executable, str(ROOT / "eng/smoke-export.py"), preset,
                            "--folder", str(extracted)], check=True)
    metadata = {"schema_version": 1, "version": version, "commit": commit, "platform": preset,
                "file": archive.name, "bytes": archive.stat().st_size, "sha256": sha256(archive),
                "headless_smoke": "PASS" if smoke else "NOT_RUN"}
    write_json(output / f"package-{preset}.json", metadata)
    mpq_kit(output, version, commit)
    return metadata


def prepare(folder, version, commit, repository, run_url):
    check_version(version)
    if not re.fullmatch(r"[0-9a-f]{40}", commit) or not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository):
        raise ValueError("Invalid commit or repository")
    packages = []
    for preset in PLATFORMS:
        metadata = json.loads((folder / f"package-{preset}.json").read_text(encoding="utf-8"))
        expected = package_name(version, preset)
        if (metadata.get("schema_version"), metadata.get("version"), metadata.get("commit"), metadata.get("platform"), metadata.get("file")) != (1, version, commit, preset, expected):
            raise ValueError(f"Package provenance mismatch: {preset}")
        archive = folder / expected
        if metadata.get("bytes") != archive.stat().st_size or metadata.get("sha256") != sha256(archive):
            raise ValueError(f"Package checksum mismatch: {preset}")
        if metadata.get("headless_smoke") != "PASS":
            raise ValueError(f"Extracted package has not passed headless smoke: {preset}")
        packages.append(metadata)
    release_url = f"https://github.com/{repository}/releases/tag/{version}"
    mpq_kit(folder, version, commit)
    write_json(folder / "release-manifest.json", {
        "schema_version": 2, "version": version, "commit": commit,
        "repository": repository, "build_run": run_url, "release_url": release_url,
        "gui_qa_status": "NOT_RUN", "gui_qa_scope": "synthetic_preview",
        "qa_plan": "PLAY-17", "required_case_ids": [key for key, _, required in CASES if required],
        "legacy_scene_qa_status": "NOT_RUN", "original_rules_validated": False,
        "two_hour_soak_status": "NOT_RUN", "packages": packages,
    })
    write_json(folder / "qa-result-template.json", {
        "schema_version": 2, "version": version, "commit": commit,
        "platform": "", "package_sha256": "", "status": "NOT_RUN",
        "status_scope": "synthetic_preview", "qa_plan": "PLAY-17",
        "legacy_scene_qa": {"status": "NOT_RUN", "scene_json_sha256": "", "content_id": "",
                            "profile": "", "asset_report": "", "source_hashes": [],
                            "original_rules_validated": False},
        "two_hour_soak": {"status": "NOT_RUN", "active_seconds": 0, "evidence": []},
        "environment": {"os": "", "architecture": "", "gpu": "", "display": "", "sdk_present": None},
        "started_utc": "", "finished_utc": "", "action_log": "", "limitations": [],
        "cases": [{"id": key, "title": title, "required": required, "status": "NOT_RUN",
                   "expected": "", "actual": "", "evidence": []} for key, title, required in CASES],
    })
    for source, target in (("docs/migration/RELEASE_QA.md", "GROK_BOT_SETUP.md"),
                           ("eng/qa/grok-task.md", "GROK_TASK.md"),
                           ("docs/migration/PLAY_INTEGRATION.md", "LEGACY_PLAY_SETUP.md"),
                           ("docs/migration/RC4_QA.md", "FEATURE_QA.md"),
                           ("LICENSE", "LICENSE"), ("THIRD_PARTY_NOTICES.md", "THIRD_PARTY_NOTICES.md")):
        shutil.copyfile(ROOT / source, folder / target)
    notes = f"""# OpenD2 {version} — GUI QA pending

Source: `{commit}` | [Build and headless checks]({run_url})

This is an experimental release candidate, not an accepted production release.
All three desktop packages passed the build/contracts and extracted-package headless checks.
**Grok Bot / visible gameplay QA: NOT_RUN.** Creating this release does not run a bot.

Download the archive for your OS and compare it with `SHA256SUMS` / `release-manifest.json`.
Linux: extract the tar.gz into a new directory and run `./OpenD2.x86_64`.
Windows: extract the entire ZIP and run `OpenD2.exe`.
macOS: extract the ZIP and open the app; this development build is not signed/notarized.
SDK, Godot editor, Python and NPC model downloads are not needed to play the synthetic preview.
The Simulation tab opens first with New game / Continue / Load checkpoint.
The HUD shows health, mana, region and play/pause state.

Features since rc.3: scene creation and actor/NPC/HUD/item artwork configuration (PLAY-08–13),
10x4 grid inventory (PLAY-14), Power strike and mana (PLAY-15), recovery potions and a four-slot
belt (PLAY-16), separated ground names and selected-item approach/pickup (PLAY-17).
Click a loot name to approach that item; L toggles names and right-click cancels movement.
F picks up the nearest item in range. Q uses the selected skill; 1–4 use belt potions once per press.
Godot 4.7.2 includes the desktop Android-polling lifecycle fix; strict ERROR checks remain enabled.
Save schema 4 / rules 7 reads validated older preview saves; back up your profile before testing.
Original Diablo II save-body import and original game rule parity are not implemented.
See FEATURE_QA.md for the new mandatory GUI cases QA-11–14 and the candidate scope.
FPS/fullscreen/diagnostics preferences apply immediately; choose Save settings to keep them.
Use F11 to leave fullscreen and Show diagnostics for seed, replay, tick and state hashes.
Master/effects/music volume and mute are saved with Save settings. Synthetic scenes use short effect tones.
Optional legacy Audio mappings load PCM WAV effects and looping region music from owned MPQs.
Actual listening/device checks are pending; headless audio checks do not certify audible output.

For Grok Bot, provide [this release]({release_url}), `GROK_TASK.md` and `qa-result-template.json`.
For public-demo MPQ terrain QA, also use `mpq-qa-kit.zip` and its MPQ_GROK_TASK.md.
The kit checks local MPQ identity and prepares a separate NOT_RUN GUI report; no game assets are included.
See `GROK_BOT_SETUP.md` for setup and the currently supported handoff.
Return screenshots/video, game logs, an action log and a completed result JSON.
FAIL, BLOCKED, NOT_RUN or missing evidence must not be treated as a pass.

Default QA scope: synthetic Camp/Cellar preview, including bounded replay rollover without stopping play.
Legacy scene loading also connects DS1/DT1 terrain, optional DCC/COF actor art and click navigation.
Supply owned game data and a verified scene JSON using `LEGACY_PLAY_SETUP.md` to test that path.
No original game assets are included. Preview combat/items are not original Diablo II rule parity.
Manifest/report schema 2 keeps synthetic GUI, legacy scene and two-hour soak acceptance separate.
Passing the default GUI cases does not accept the original campaign or original assets.
Optional NPC AI remains experimental. Windows/macOS installation, GPU performance,
original assets and long-duration acceptance require their own evidence.
"""
    (folder / "RELEASE_NOTES.md").write_text(notes, encoding="utf-8")
    # Cover documentation and manifests as well as the exact archives handed to the bot.
    files = sorted(p for p in folder.iterdir() if p.is_file() and p.name != "SHA256SUMS")
    (folder / "SHA256SUMS").write_text("".join(f"{sha256(p)}  {p.name}\n" for p in files), encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    version = commands.add_parser("check-version")
    version.add_argument("version")
    package = commands.add_parser("pack")
    package.add_argument("--preset", choices=PLATFORMS, required=True)
    package.add_argument("--source", type=Path)
    package.add_argument("--output", type=Path, default=ROOT / "artifacts/packages")
    package.add_argument("--smoke", action="store_true")
    handoff = commands.add_parser("prepare")
    handoff.add_argument("--folder", type=Path, default=ROOT / "artifacts/release")
    args = parser.parse_args()
    if args.command == "check-version":
        check_version(args.version)
    elif args.command == "pack":
        pack(args.source or ROOT / "artifacts" / args.preset, args.output, args.preset,
             os.environ.get("RELEASE_TAG", "ci"), os.environ["SOURCE_SHA"], args.smoke)
    else:
        repository = os.environ["GITHUB_REPOSITORY"]
        run_url = f"https://github.com/{repository}/actions/runs/{os.environ['GITHUB_RUN_ID']}"
        prepare(args.folder, os.environ["RELEASE_TAG"], os.environ["SOURCE_SHA"], repository, run_url)
    print(f"RELEASE {args.command.upper()} OK")


if __name__ == "__main__":
    main()
