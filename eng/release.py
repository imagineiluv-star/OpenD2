"""Package validated exports and servers and produce a reviewable, explicitly untested GUI/online QA handoff."""
import argparse
import gzip
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
import time
import zipfile

import data_guard

ROOT = Path(__file__).resolve().parents[1]
PLATFORMS = {"Linux": "linux-x64.tar.gz", "Windows": "windows-x64.zip", "macOS": "macos-universal.zip"}
# The online server is published self-contained per OS runtime (macOS CI runs on arm64; an Intel Mac build is not produced).
SERVER_RUNTIMES = {"Linux": "linux-x64", "Windows": "win-x64", "macOS": "osx-arm64"}
# Recorded as PASS by eng/pack-online-server.py after it ran the EXTRACTED server with packaged clients, TLS and the
# network/capacity/world contracts. A server package missing any of them is not handed off.
SERVER_REQUIRED_PASS = (
    "extracted_server_and_two_clients", "two_client_gameplay", "tls_certificates", "tls_two_client_gameplay",
    "open_tls_two_client_gameplay", "realm_invite_to_gameplay", "open_invite_to_gameplay", "host_and_guest_reconnect",
    "cooperative_world_realm", "cooperative_world_open", "cooperative_world_network_realm", "cooperative_world_network_open",
    "network_relay_realm", "network_relay_open", "silent_stream_realm", "silent_stream_open",
    "realm_32_stream_admission", "open_32_stream_admission", "world_realm_32_stream_admission", "world_open_32_stream_admission",
    "lan_host_tls_lifecycle", "personal_host_lifecycle", "room_stream_contracts")
# Never run for this package; a package that claims otherwise is rejected rather than trusted.
SERVER_MUST_BE_NOT_RUN = ("physical_multi_pc", "wan", "packet_loss", "maximum_server_capacity", "gui", "clipboard_gestures")
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
    # Online acceptance needs the server package and a second client (second window or second PC): scheduled separately.
    ("QA-15", "Realm server: extracted server, two accounts, room, invite, combat/loot and reconnect", False),
    ("QA-16", "Open host: personal and LAN host from the client, guest on another PC, character file export/import", False),
    ("QA-17", "Cooperative world: Camp/Field portals, personal quest accept/complete per player, reconnect and server restart", False),
    ("QA-18", "Server package on each OS: extract, run with a private data folder, TLS and graceful stop", False),
]


TAG_PATTERN = r"v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-rc\.[1-9][0-9]*)?"


def read_version(root=ROOT):
    """The root VERSION file is the single version source (X.Y.Z, no prefix or suffix)."""
    value = (root / "VERSION").read_text(encoding="utf-8").strip()
    if not re.fullmatch(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)", value):
        raise ValueError(f"VERSION must be X.Y.Z, got {value!r}")
    return value


def check_version(version, allow_ci=False):
    """Accept a stable tag (v0.1.0) or a candidate (v0.1.0-rc.1)."""
    if (allow_ci and version == "ci") or re.fullmatch(TAG_PATTERN, version):
        return version
    raise ValueError("Expected a release tag such as v0.1.0 or v0.1.0-rc.1")


def check_tag_matches_version(version, root=ROOT):
    """Fail unless the tag's X.Y.Z equals the VERSION file."""
    check_version(version)
    expected = read_version(root)
    if version.split("-", 1)[0][1:] != expected:
        raise ValueError(f"Tag {version} does not match VERSION {expected}")
    return version


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def write_json(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def package_name(version, preset):
    return f"OpenD2-{version}-{PLATFORMS[preset]}"


def server_name(version, preset):
    return f"OpenD2-{version}-server-{SERVER_RUNTIMES[preset]}.zip"


def server_package(folder, version, commit, preset):
    """Verify one CI-built server package, give it the versioned name and return its release metadata."""
    runtime = SERVER_RUNTIMES[preset]
    archive = folder / f"OpenD2-server-{runtime}.zip"
    info_path = folder / f"server-{runtime}.json"
    info = json.loads(info_path.read_text(encoding="utf-8"))
    if (info.get("commit"), info.get("runtime"), info.get("file")) != (commit, runtime, archive.name):
        raise ValueError(f"Server package provenance mismatch: {preset}")
    if info.get("bytes") != archive.stat().st_size or info.get("sha256") != sha256(archive):
        raise ValueError(f"Server package checksum mismatch: {preset}")
    missing = [key for key in SERVER_REQUIRED_PASS if info.get(key) != "PASS"]
    if missing:
        raise ValueError(f"Server package lacks passing validation ({', '.join(missing)}): {preset}")
    overclaimed = [key for key in SERVER_MUST_BE_NOT_RUN if info.get(key) != "NOT_RUN"]
    if overclaimed:
        raise ValueError(f"Server package claims validation that was not run ({', '.join(overclaimed)}): {preset}")
    target = folder / server_name(version, preset)
    if target.exists():
        raise FileExistsError(target)
    archive.rename(target)  # the same bytes under the release name
    info_path.unlink()
    metadata = {"schema_version": 1, "version": version, "commit": commit, "platform": preset, "runtime": runtime,
                "file": target.name, "bytes": target.stat().st_size, "sha256": sha256(target),
                "validation": {key: value for key, value in sorted(info.items())
                               if key not in ("commit", "runtime", "file", "bytes", "sha256")}}
    write_json(folder / f"server-package-{preset}.json", metadata)
    return metadata


def mpq_kit(output, version, commit):
    # Only authored configuration, metadata and instructions; never game data.
    path = output / "mpq-qa-kit.zip"
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as archive:
        for name in ("mpq_handoff.py", "demo-mpq-hashes.json", "demo-town-scene.json", "MPQ_GROK_TASK.md"):
            archive.writestr(zipfile.ZipInfo(name, (2026, 1, 1, 0, 0, 0)), (ROOT / "eng/qa" / name).read_bytes())
        archive.writestr(zipfile.ZipInfo("kit.json", (2026, 1, 1, 0, 0, 0)),
                         json.dumps({"schema_version": 1, "version": version, "commit": commit}))
    return path


def source_date_epoch():
    """SOURCE_DATE_EPOCH (set to the commit time by eng/release-local.py), else a fixed 2026-01-01 stamp."""
    return max(int(os.environ.get("SOURCE_DATE_EPOCH", "1767225600")), 315532800)  # ZIP cannot go before 1980


def reproducible_tar(stage, archive):
    """Same bytes for the same staged tree: sorted entries, fixed owner and mtime, gzip header without time/name."""
    epoch = source_date_epoch()

    def normalize(info):
        info.uid = info.gid = 0
        info.uname = info.gname = ""
        info.mtime = epoch
        info.mode = 0o755 if info.isdir() or info.mode & 0o111 else 0o644
        return info

    with archive.open("wb") as raw, gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=0) as compressed, \
            tarfile.open(fileobj=compressed, mode="w", format=tarfile.PAX_FORMAT) as target:
        for item in sorted(stage.rglob("*"), key=lambda p: p.relative_to(stage).as_posix()):
            target.add(item, arcname=item.relative_to(stage).as_posix(), recursive=False, filter=normalize)


def reproducible_zip(stage, archive):
    stamp = time.gmtime(source_date_epoch())[:6]
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as target:
        for item in sorted(stage.rglob("*"), key=lambda p: p.relative_to(stage).as_posix()):
            if item.is_dir():
                continue
            info = zipfile.ZipInfo(item.relative_to(stage).as_posix(), stamp)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = (0o100755 if item.stat().st_mode & 0o111 else 0o100644) << 16
            target.writestr(info, item.read_bytes())


def pack(source, output, preset, version, commit, smoke=False, rehearsal=None):
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
                reproducible_tar(stage, archive)
            else:
                reproducible_zip(stage, archive)
    # Fail closed on game data anywhere in the archive, including nested archives and PCK file tables.
    # A real release requires the original hash list; the untagged "ci" build or --rehearsal only warns.
    if rehearsal is None:
        rehearsal = version == "ci" or os.environ.get("OPEND2_RELEASE_REHEARSAL") == "1"
    try:
        guard = data_guard.scan([archive], data_guard.load_original_hashes(), require_original_hashes=not rehearsal,
                                derived_hashes=data_guard.load_derived_hashes())
        guard.check()
    except BaseException:
        archive.unlink(missing_ok=True)
        raise
    if not rehearsal and not guard.derived:
        guard.warnings.append("no extracted asset-cache hashes (no ~/.cache/opend2/asset-cache here); copied DC6/TXT/WAV "
                              "extracts are caught only by name and content magic")
    for warning in guard.warnings:
        print(f"WARNING data guard: {warning}")
    if not guard.original:
        print("WARNING data guard: no original hash list (rehearsal); renamed originals are caught only by name/MPQ header")
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
                "headless_smoke": "PASS" if smoke else "NOT_RUN",
                "data_guard": {"result": guard.result, "warnings": guard.warnings, "asset_cache_hashes": len(guard.derived), "unknown_formats": guard.unknown, "entries": guard.scanned, "original_hashes": len(guard.original)}}
    write_json(output / f"package-{preset}.json", metadata)
    mpq_kit(output, version, commit)
    return metadata


def is_release_candidate(version):
    return "-rc." in version


def _version_key(tag):
    match = re.fullmatch(r"v(\d+)\.(\d+)\.(\d+)(?:-rc\.(\d+))?", tag)
    if not match:
        return None
    major, minor, patch, rc = match.groups()
    # A stable X.Y.Z sorts after every X.Y.Z-rc.N.
    return int(major), int(minor), int(patch), int(rc) if rc else float("inf")


def previous_tag(version, tags=None):
    """The highest existing release tag below version, or None for the first release."""
    if tags is None:
        listed = subprocess.run(["git", "tag", "--list", "v*"], cwd=ROOT, capture_output=True, text=True,
                                encoding="utf-8")
        tags = listed.stdout.split() if listed.returncode == 0 else []
    current = _version_key(version)
    older = [tag for tag in tags if (key := _version_key(tag)) is not None and current is not None and key < current]
    return max(older, key=_version_key) if older else None


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
    servers = [server_package(folder, version, commit, preset) for preset in SERVER_RUNTIMES]
    release_url = f"https://github.com/{repository}/releases/tag/{version}"
    mpq_kit(folder, version, commit)
    write_json(folder / "release-manifest.json", {
        "schema_version": 3, "version": version, "commit": commit,
        "repository": repository, "build_run": run_url, "release_url": release_url,
        "gui_qa_status": "NOT_RUN", "gui_qa_scope": "synthetic_preview",
        "qa_plan": "RC5", "required_case_ids": [key for key, _, required in CASES if required],
        "online_qa_status": "NOT_RUN", "online_case_ids": ["QA-15", "QA-16", "QA-17", "QA-18"],
        "legacy_scene_qa_status": "NOT_RUN", "original_rules_validated": False,
        "two_hour_soak_status": "NOT_RUN", "packages": packages, "server_packages": servers,
    })
    write_json(folder / "qa-result-template.json", {
        "schema_version": 3, "version": version, "commit": commit,
        "platform": "", "package_sha256": "", "status": "NOT_RUN",
        "status_scope": "synthetic_preview", "qa_plan": "RC5", "online_qa_status": "NOT_RUN",
        "server_package_sha256": "",
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
                           ("docs/migration/RC5_QA.md", "FEATURE_QA.md"),
                           ("LICENSE", "LICENSE"), ("THIRD_PARTY_NOTICES.md", "THIRD_PARTY_NOTICES.md")):
        shutil.copyfile(ROOT / source, folder / target)
    candidate = is_release_candidate(version)
    previous = previous_tag(version)
    since = previous or "the previous preview"
    status_line = ("This is an experimental release candidate, not an accepted production release." if candidate else
                   f"This is the {version} release build; GUI QA results are recorded separately in release-manifest.json.")
    notes = f"""# OpenD2 {version}{" — GUI QA pending" if candidate else ""}

Source: `{commit}` | [Build and headless checks]({run_url})

{status_line}
All three desktop packages passed the build/contracts and extracted-package headless checks.
**Grok Bot / visible gameplay QA: NOT_RUN.** Creating this release does not run a bot.

Download the archive for your OS and compare it with `SHA256SUMS` / `release-manifest.json`.
Linux: extract the tar.gz into a new directory and run `./OpenD2.x86_64`.
Windows: extract the entire ZIP and run `OpenD2.exe`.
macOS: extract the ZIP and open the app; this development build is not signed/notarized.
SDK, Godot editor, Python and NPC model downloads are not needed to play the synthetic preview.
The Simulation tab opens first with New game / Continue / Load checkpoint.
The HUD shows health, mana, region and play/pause state.

Features since {since}: the online preview. A self-contained **server** is now part of this release
(`OpenD2-<version>-server-<runtime>.zip` for linux-x64, win-x64 and osx-arm64). Realm (operator-run) and Open
(personal or LAN host) modes, accounts, characters, rooms with invites and reconnect, WSS streams, Open character
files, and a synthetic cooperative Camp/Field world with personal quests. Characters can have one of seven classes with
levels 1-99, attribute and skill points and monster kinds with levels and kill experience (Open file format 4); the
tables are built-in previews and are NOT Diablo II data, and the client shows them in a developer panel only. Operators can run the
server on character tables they extracted from their own install with `--tables <folder>` (see CHARACTER_PROGRESSION.md in the server
package); that path has not been tried with real Diablo II files. Server hardening: login hashing and
checkpoint writes no longer stall rooms, and restart, network-fault and 32-stream capacity behaviour are
covered by contract tests (see the server package's documentation).
Carried over from earlier previews: scene creation and artwork configuration (PLAY-08–13), 10x4 grid inventory, Power strike
and mana, recovery potions and a four-slot belt, separated ground names. F picks up the nearest item in range.
Q uses the selected skill; 1–4 use belt potions once per press. L toggles ground names.
Godot 4.7.2 includes the desktop Android-polling lifecycle fix; strict ERROR checks remain enabled.
Save schema 5 / rules 8 reads validated older preview saves; back up your profile before testing.
Existing cooperative-world server data from `cooperative-demo-v1` is not read by this server (content changed to v2).
Original Diablo II save-body import and original game rule parity are not implemented.
See FEATURE_QA.md for the mandatory GUI cases QA-01–06, 08, 11–14 and the separately scheduled online cases QA-15–18.
**Online QA: NOT_RUN.** The server packages passed the extracted-server, TLS, two-client, reconnect, cooperative-world,
network-fault and 32-stream checks in CI. They have not been run across two physical PCs, over a WAN, or by a person.
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
    package.add_argument("--rehearsal", action="store_true", help="allow packing without the original hash list")
    handoff = commands.add_parser("prepare")
    handoff.add_argument("--folder", type=Path, default=ROOT / "artifacts/release")
    args = parser.parse_args()
    if args.command == "check-version":
        check_tag_matches_version(args.version)
    elif args.command == "pack":
        pack(args.source or ROOT / "artifacts" / args.preset, args.output, args.preset,
             os.environ.get("RELEASE_TAG", "ci"), os.environ["SOURCE_SHA"], args.smoke, True if args.rehearsal else None)
    else:
        repository = os.environ["GITHUB_REPOSITORY"]
        run_url = f"https://github.com/{repository}/actions/runs/{os.environ['GITHUB_RUN_ID']}"
        prepare(args.folder, os.environ["RELEASE_TAG"], os.environ["SOURCE_SHA"], repository, run_url)
    print(f"RELEASE {args.command.upper()} OK")


if __name__ == "__main__":
    main()
