"""Reproduce public-demo MPQ checks; never execute the installer or claim GUI acceptance."""
import argparse
import ctypes as c
import json
import os
from pathlib import Path
import platform
import subprocess
import sys
import urllib.request

from mpq_handoff import CONTENT_ID, digest, read_json, verify_data

ROOT = Path(__file__).resolve().parents[2]
URL = "https://www.classicdosgames.com/files/games/blizzard/DiabloIIDemo.exe"
INSTALLER_BYTES = 138309685
INSTALLER_SHA256 = "89352716523e474514553e2092a1ae9349c5c7ff9e79c7861dd65fe19be88b61"
MISSING = {"d2xmusic.mpq", "d2xtalk.mpq", "d2xvideo.mpq", "d2exp.mpq", "d2video.mpq"}


def checked_installer(path):
    if path.stat().st_size != INSTALLER_BYTES or digest(path) != INSTALLER_SHA256:
        raise ValueError("Public-demo installer size/SHA256 mismatch")


def download(path):
    with urllib.request.urlopen(URL, timeout=60) as source, path.open("xb") as target:
        count = 0
        while block := source.read(1024 * 1024):
            count += len(block)
            if count > INSTALLER_BYTES:
                raise ValueError("Installer download exceeds pinned size")
            target.write(block)
    checked_installer(path)


def extract(installer, target):
    name = {"Windows": "opend2_mpq.dll", "Darwin": "libopend2_mpq.dylib", "Linux": "libopend2_mpq.so"}[platform.system()]
    lib = c.CDLL(str(ROOT / ".local-tools/native/dist" / name))
    lib.od2_open.argtypes = [c.c_char_p, c.POINTER(c.c_void_p)]
    lib.od2_close.argtypes = [c.c_void_p]
    lib.od2_file_open.argtypes = [c.c_void_p, c.c_char_p, c.POINTER(c.c_void_p), c.POINTER(c.c_uint64)]
    lib.od2_file_close.argtypes = [c.c_void_p]
    lib.od2_read.argtypes = [c.c_void_p, c.c_void_p, c.c_uint32, c.POINTER(c.c_uint32)]
    handle = c.c_void_p()
    if lib.od2_open(str(installer).encode("utf-8"), c.byref(handle)):
        raise ValueError("Cannot read installer archive")
    target.mkdir()
    try:
        for entry in read_json(ROOT / "eng/qa/demo-mpq-hashes.json"):
            file, size = c.c_void_p(), c.c_uint64()
            logical = ("SetupDat\\Files\\" + entry["name"]).encode("ascii")
            if lib.od2_file_open(handle, logical, c.byref(file), c.byref(size)):
                raise ValueError("Missing installer entry: " + entry["name"])
            try:
                if size.value != entry["bytes"]:
                    raise ValueError("Unexpected embedded MPQ size")
                with (target / entry["name"]).open("xb") as output:
                    remaining = size.value
                    buffer = c.create_string_buffer(1024 * 1024)
                    while remaining:
                        length = min(remaining, len(buffer))
                        count = c.c_uint32()
                        code = lib.od2_read(file, buffer, length, c.byref(count))
                        if code or count.value != length:
                            raise ValueError("Truncated embedded MPQ")
                        output.write(buffer.raw[:length])
                        remaining -= length
            finally:
                lib.od2_file_close(file)
    finally:
        lib.od2_close(handle)
    return verify_data(target)


def terrain_errors(report, code):
    errors = []
    if code != 3:
        errors.append("expected explicit LoD missing-archive exit 3 for this pinned demo")
    install = report.get("Installation", {})
    if set(install.get("MissingArchives", [])) != MISSING or install.get("UnclassifiedArchives"):
        errors.append("unexpected installation contents")
    ready = report.get("Readiness", {})
    if (report.get("ContentId") != CONTENT_ID or not ready.get("QuestLoopReachable")
            or ready.get("Issues") != ["actor_artwork_missing:1", "actor_artwork_missing:2"]):
        errors.append("scene identity/connectivity/readiness changed")
    maps = report.get("Maps", [])
    if len(maps) != 1:
        errors.append("expected one terrain region")
    else:
        terrain = maps[0].get("Check", {})
        if (terrain.get("Width"), terrain.get("Height"), terrain.get("MissingTiles"), terrain.get("DuplicateKeys")) != (57, 41, 0, 77):
            errors.append("terrain dimensions/tile coverage changed")
    return errors


def run(args):
    work, output = args.work.resolve(), args.output.resolve()
    if work == output or work.is_relative_to(output) or output.is_relative_to(work):
        raise ValueError("Keep private input and report directories separate")
    work.mkdir(parents=True, exist_ok=False)
    output.mkdir(parents=True, exist_ok=False)
    summary = {"schema_version": 1, "mode": args.mode, "status": "FAIL", "gui_qa": "NOT_RUN",
               "full_compatibility": "NOT_VERIFIED", "full_audit": "NOT_RUN", "installer_executed": False,
               "installer_sha256": INSTALLER_SHA256, "source_url": URL,
               "commit": os.environ.get("GITHUB_SHA", "local"), "platform": platform.system()}
    try:
        installer = args.installer.resolve() if args.installer else work / "DiabloIIDemo.exe"
        if args.installer:
            checked_installer(installer)
        else:
            download(installer)
        data = work / "mpq"
        summary["mpq_hashes"] = extract(installer, data)
        assembly = ROOT / "tools/OpenD2.AssetAudit/bin/Release/net10.0/OpenD2.AssetAudit.dll"
        command = [args.dotnet, str(assembly)]
        if args.mode == "terrain":
            command += ["--check-scene", str(data), str(ROOT / "eng/qa/demo-town-scene.json")]
        else:
            command += ["--decode-demo", str(data)]
        result = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", timeout=600)
        # Preserve all diagnostics; redact only the private input root from reports.
        stdout, stderr = result.stdout, result.stderr
        for text_path in (str(data), str(work)):
            stdout = stdout.replace(json.dumps(text_path)[1:-1], "<private-data>").replace(text_path, "<private-data>")
            stderr = stderr.replace(text_path, "<private-data>")
        (output / "audit.json").write_text(stdout, encoding="utf-8")
        (output / "audit.stderr.txt").write_text(stderr, encoding="utf-8")
        summary["raw_exit_code"] = result.returncode
        report = json.loads(stdout)
        if args.mode == "terrain":
            summary["scope"] = "public_demo_terrain_only"
            errors = terrain_errors(report, result.returncode)
            if stderr.strip():
                errors.append("unexpected decoder stderr; see audit.stderr.txt")
            summary["errors"] = errors
            summary["status"] = "FAIL" if errors else "PASS"
            summary["content_id"] = report.get("ContentId")
            summary["limitations"] = ["Known LoD archives absent; raw scene check remains exit 3",
                                      "Actor art absent; terrain-only acceptance is not full QA-09 or GUI PASS"]
        else:
            summary["scope"] = "strict_demo_inventory_audit"
            failed = (result.returncode != 0 or bool(stderr.strip()) or bool(report["Installation"]["MissingArchives"])
                      or report["Installation"].get("Profile") != "demo-1.04"
                      or bool(report["Installation"].get("UnclassifiedArchives"))
                      or any(a["ErrorCode"] for a in report["Archives"]) or any(e["ErrorCode"] for e in report["Entries"]))
            summary["full_audit"] = summary["status"] = "FAIL" if failed else "PASS"
            summary["opaque_records"] = [{k: e[k] for k in ("SourceArchive", "LogicalPath", "ContentHash", "DecodeStatus")}
                                         for e in report["Entries"] if e.get("Format") == "demo_opaque_record"]
            summary["limitations"] = ["Demo archive profile only; not retail/LoD compatibility",
                                      "Opaque records receive exact integrity validation, not audio decoding",
                                      "Coverage limited to enumerated paths and implemented formats; GUI and runtime audio NOT_RUN"]
            summary["decoder_failures"] = [{k: e[k] for k in ("SourceArchive", "LogicalPath", "ErrorCode", "Size")}
                                           for e in report["Entries"] if e["ErrorCode"]]
        return 0 if summary["status"] == "PASS" else 3
    except (OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError) as error:
        summary["error"] = str(error).replace(str(work), "<private-data>")
        return 1
    finally:
        (output / "summary.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        text = (f"## MPQ {args.mode}: {summary['status']}\n\n"
                f"Scope: {summary.get('scope', args.mode)}; GUI: NOT_RUN; full compatibility: NOT_VERIFIED.\n\n"
                f"Raw decoder exit: {summary.get('raw_exit_code', 'not executed')}; full audit: {summary['full_audit']}.\n"
                "See summary.json and audit.json; no MPQs or extracted game assets are uploaded.\n")
        (output / "summary.md").write_text(text, encoding="utf-8")
        if os.environ.get("GITHUB_STEP_SUMMARY"):
            with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as target:
                target.write(text)
        print(text)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mode", choices=("terrain", "full-audit"), required=True)
    parser.add_argument("--work", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--installer", type=Path, help="Optional local copy; same pinned checksum required")
    parser.add_argument("--dotnet", default="dotnet")
    sys.exit(run(parser.parse_args()))
