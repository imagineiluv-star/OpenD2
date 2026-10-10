"""Read-only public-demo identity check and terrain-only GUI QA handoff (Python 3.12+)."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import sys

HERE = Path(__file__).resolve().parent
CONTENT_ID = "ce384cadb59d616824b0fc2c8c92199a785f666a3d65bb7b567ed309b16636de"
CASES = {
    "MPQ-01": "Load supplied scene in the visible packaged app; terrain/palette and paused menu",
    "MPQ-02": "Continue, click ground/wall, move in eight directions and inspect occlusion",
    "MPQ-03": "Preview guide interaction, monster combat and quest completion in this single region",
    "MPQ-04": "Save, exit, reload the same scene/checkpoint; compare scene ID and state",
    "MPQ-05": "Resize/fullscreen, pause/menu/resume; record visible defects and errors",
}


def read_json(path):
    if path.stat().st_size > 1024 * 1024:
        raise ValueError("JSON exceeds 1 MiB")
    return json.loads(path.read_text(encoding="utf-8"))


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def verify_data(data):
    expected = read_json(HERE / "demo-mpq-hashes.json")
    wanted = {entry["name"]: entry for entry in expected}
    actual = {}
    for path in data.iterdir():
        if path.suffix.lower() != ".mpq":
            continue
        key = path.name.lower()
        if key in actual or path.is_symlink() or not path.is_file():
            raise ValueError("Duplicate, linked or non-file MPQ")
        actual[key] = path
        if len(actual) > len(wanted):
            raise ValueError("Extra MPQs would change archive priority; use an isolated demo directory")
    if set(actual) != set(wanted):
        raise ValueError("Expected exactly the six public-demo MPQs; retail/LoD needs a separate validated scene")
    for key, path in actual.items():
        before = path.stat()
        if before.st_size != wanted[key]["bytes"] or digest(path) != wanted[key]["sha256"]:
            raise ValueError("Demo identity mismatch: " + key)
        after = path.stat()
        if (before.st_size, before.st_mtime_ns) != (after.st_size, after.st_mtime_ns):
            raise ValueError("MPQ changed while checking: " + key)
    return expected


def verify_package(package, metadata):
    value = read_json(metadata)
    if (value.get("schema_version") != 1 or value.get("platform") not in ("Windows", "Linux", "macOS")
            or value.get("headless_smoke") != "PASS"
            or not re.fullmatch(r"[0-9a-f]{40}", value.get("commit", ""))):
        raise ValueError("Expected package-<platform>.json with a completed extracted-package smoke")
    if (value.get("file") != package.name or value.get("bytes") != package.stat().st_size
            or value.get("sha256") != digest(package)):
        raise ValueError("Package checksum/size/name mismatch")
    kit = HERE / "kit.json"
    if kit.exists():
        identity = read_json(kit)
        if (identity.get("commit"), identity.get("version")) != (value["commit"], value.get("version")):
            raise ValueError("QA kit and package come from different builds")
    return value


def prepare(data, package, metadata, output, include_music=False):
    data, output = data.resolve(), output.resolve()
    if output == data or output.is_relative_to(data):
        raise ValueError("Output must be outside the MPQ directory")
    if output.exists():
        raise FileExistsError("Use a new QA output directory; existing evidence is never replaced")
    source_hashes = verify_data(data)
    provenance = verify_package(package, metadata)
    scene_path = HERE / "demo-town-scene.json"
    scene_bytes = scene_path.read_bytes()
    cases = dict(CASES)
    if include_music:
        scene = json.loads(scene_bytes)
        scene['Audio'] = {'Effects': [], 'Music': [{'Region': 1, 'Path': 'data/global/music/act1/town1.wav'}]}
        scene_bytes = (json.dumps(scene, ensure_ascii=False, indent=2) + "\n").encode('utf-8')
        cases['MPQ-06'] = 'Listen to town music, pause/resume, mute/volume, stop/reload; retain audio evidence'
    scene_hash = hashlib.sha256(scene_bytes).hexdigest()
    report = {
        "schema_version": 1, "scope": "public_demo_terrain_music_preview" if include_music else "public_demo_terrain_preview", "status": "NOT_RUN",
        "package": provenance, "dataset_identity": "public-demo-1.04-declared",
        "mpq_integrity": "PASS", "mpq_hashes": source_hashes,
        "scene_json_sha256": scene_hash, "expected_content_id": CONTENT_ID,
        "observed_content_id": "", "gui_qa": "NOT_RUN", "full_lod_compatibility": "NOT_VERIFIED",
        "original_rules_validated": False, "actor_artwork": "BLOCKED", "audio_qa": "NOT_RUN" if include_music else "BLOCKED",
        "full_asset_audit": "NOT_RUN", "environment": {"os": "", "cpu": "", "gpu": "", "display": ""},
        "started_utc": "", "finished_utc": "", "action_log": "action-log.txt",
        "cases": [{"id": key, "expected": title, "status": "NOT_RUN", "actual": "", "evidence": []}
                  for key, title in cases.items()],
        "limitations": ["Identity check is not MPQ decoding or GUI acceptance",
                        "Historical Linux audit exit 3: missing LoD archives and three WAV rejections",
                        "Scene uses current lod-1.10f decoder contract, not certified demo compatibility",
                        "Actors/NPC/HUD/items are placeholders; only optional town music is configured",
                        "No portal in this single-region preview; synthetic rules, not original campaign"],
    }
    output.mkdir(parents=True, exist_ok=False)
    try:
        (output / "scene.json").write_bytes(scene_bytes)
        shutil.copyfile(HERE / "MPQ_GROK_TASK.md", output / "MPQ_GROK_TASK.md")
        (output / "mpq-qa-result.json").write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        (output / "action-log.txt").write_text("", encoding="utf-8")
        (output / "screenshots").mkdir()
        (output / "logs").mkdir()
    except Exception:
        shutil.rmtree(output)
        raise
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--data", type=Path, required=True)
    parser.add_argument("--package", type=Path, required=True)
    parser.add_argument("--package-metadata", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--music", action="store_true", help="Include demo town music and separate listening case")
    args = parser.parse_args()
    try:
        report = prepare(args.data, args.package, args.package_metadata, args.output, args.music)
        print(json.dumps({"mpq_integrity": report["mpq_integrity"], "gui_qa": report["gui_qa"],
                          "scope": report["scope"], "scene_json_sha256": report["scene_json_sha256"]}))
        return 0
    except (OSError, ValueError, TypeError, KeyError) as error:
        print("MPQ QA preparation failed: " + str(error), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
