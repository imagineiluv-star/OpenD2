"""Build a release on this machine without GitHub Actions; never tags, pushes or uploads.

  python eng/release-local.py [--tag v0.1.0] [--preset Linux --preset Windows] [--skip-tests] [--rehearsal]

1. Preflight: clean work tree, the tag matches VERSION and, when the tag exists, HEAD is the tagged commit
   (--rehearsal allows an untagged HEAD and marks the notes as a rehearsal).
2. Exports HEAD (git archive) into a fixed build directory so absolute paths are identical between runs.
3. SOURCE_DATE_EPOCH = commit time; dotnet build/test (known failure allowlist) and Godot --export-release
   with VersionSuffix/SourceRevisionId/ContinuousIntegrationBuild so InformationalVersion is <version>+<sha>.
4. Packs with reproducible archives, runs the game-data guard (eng/data_guard.py; original hashes from
   OPEND2_ORIGINAL_SHA256SUMS or D2_DATA_PATH when set) and writes release-manifest.json, RELEASE_NOTES.md and
   SHA256SUMS into ../opend2-release/<tag>/ (outside the repository).
Publishing stays a manual step for the maintainer: `git tag -a`, `gh release create`.
"""
import argparse
import datetime
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
import data_guard  # noqa: E402
import release  # noqa: E402

ROOT = release.ROOT
BUILD_ROOT = Path("/tmp/opend2-release-build")
EXECUTABLES = {"Linux": "OpenD2.x86_64", "Windows": "OpenD2.exe", "macOS": "OpenD2.zip"}
# Tests that fail on a box without optional local tooling; anything else failing stops the release.
KNOWN_FAILURES = ("NPC bundled native runtime executes",)
PHASES = {(0, 1): "P1 original data + P2-1 foundation", (0, 2): "P2 original screens", (0, 3): "P3 rules and skills",
          (0, 4): "P4 levels and .d2s"}


def git(*args, cwd=ROOT):
    return subprocess.run(["git", *args], cwd=cwd, check=True, capture_output=True, text=True).stdout.strip()


def phase(version):
    major, minor = (int(x) for x in version.split(".")[:2])
    if major >= 1:
        return "1.14d parity" if (major, minor) == (1, 0) else "P6"
    return PHASES.get((major, minor), "P5 verification and 1.14d parity" if 5 <= minor <= 9 else "unplanned")


def preflight(tag, rehearsal):
    release.check_tag_matches_version(tag)
    if git("status", "--porcelain", "--untracked-files=no"):
        raise SystemExit("Work tree has uncommitted changes")
    head = git("rev-parse", "HEAD")
    tagged = subprocess.run(["git", "rev-parse", "-q", "--verify", f"refs/tags/{tag}^{{commit}}"], cwd=ROOT,
                            capture_output=True, text=True).stdout.strip()
    if tagged and tagged != head:
        raise SystemExit(f"{tag} points at {tagged}, HEAD is {head}")
    if not tagged and not rehearsal:
        raise SystemExit(f"{tag} does not exist; create the annotated tag first or pass --rehearsal")
    if tagged and git("cat-file", "-t", f"refs/tags/{tag}") != "tag":
        raise SystemExit(f"{tag} is a lightweight tag; use an annotated tag (git tag -a)")
    return head


def changelog_section(version):
    text = (ROOT / "CHANGELOG.md").read_text(encoding="utf-8")
    match = re.search(rf"^## \[{re.escape(version)}\][^\n]*\n(.*?)(?=^## \[|\Z)", text, re.S | re.M)
    return match.group(1).strip() if match else "_No CHANGELOG entry for this version yet._"


def run(command, cwd, env, log):
    with log.open("a", encoding="utf-8") as stream:
        stream.write(f"$ {' '.join(map(str, command))}\n")
        stream.flush()
        result = subprocess.run(command, cwd=cwd, env=env, stdout=stream, stderr=subprocess.STDOUT, text=True)
    return result.returncode


def build(head, tag, presets, skip_tests, log):
    source = BUILD_ROOT / "src"
    shutil.rmtree(BUILD_ROOT, ignore_errors=True)
    source.mkdir(parents=True)
    subprocess.run(f"git archive --format=tar {head} | tar -x -C {source}", shell=True, cwd=ROOT, check=True)
    tools = ROOT / ".local-tools"
    if tools.is_dir():
        (source / ".local-tools").symlink_to(tools)
    epoch = git("show", "-s", "--format=%ct", head)
    suffix = tag.partition("-")[2]
    env = dict(os.environ, SOURCE_DATE_EPOCH=epoch, SourceRevisionId=head, VersionSuffix=suffix,
               ContinuousIntegrationBuild="true", DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1")
    if run(["dotnet", "build", "OpenD2.sln", "-c", "Release", "-m:1", "-p:BuildInParallel=false"], source, env, log):
        raise SystemExit(f"dotnet build failed; see {log}")
    if not skip_tests:
        code = run(["dotnet", "run", "--project", "tests/OpenD2.Tests", "-c", "Release", "--no-build"], source, env, log)
        text = log.read_text(encoding="utf-8")
        failures = re.findall(r"^FAIL[^\n]*", text, re.M)
        unexpected = [line for line in failures if not any(known in line for known in KNOWN_FAILURES)]
        if code and (unexpected or not failures):
            raise SystemExit(f"Tests failed ({len(unexpected)} unexpected); see {log}")
    godot = (tools / "godot-path.txt").read_text(encoding="utf-8").strip()
    exports = {}
    for preset in presets:
        destination = BUILD_ROOT / "export" / preset / EXECUTABLES[preset]
        destination.parent.mkdir(parents=True)
        if run([godot, "--headless", "--path", "src/OpenD2.Client", "--export-release", preset, str(destination)],
               source, env, log) or not destination.is_file():
            raise SystemExit(f"Godot export {preset} failed; see {log}")
        exports[preset] = destination.parent
    return exports, env


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--tag", help="Release tag; default v<VERSION>")
    parser.add_argument("--preset", action="append", choices=list(EXECUTABLES))
    parser.add_argument("--output", type=Path, default=ROOT.parent / "opend2-release")
    parser.add_argument("--skip-tests", action="store_true")
    parser.add_argument("--rehearsal", action="store_true", help="Allow an untagged HEAD (notes say REHEARSAL)")
    args = parser.parse_args()
    version = release.read_version()
    tag = args.tag or f"v{version}"
    presets = args.preset or ["Linux"]
    head = preflight(tag, args.rehearsal)
    output = (args.output / tag).resolve()
    if output.is_relative_to(ROOT):
        raise SystemExit("Release output must be outside the repository")
    if output.exists():
        raise SystemExit(f"{output} exists; move it away first")
    output.mkdir(parents=True)
    log = output.parent / f"{tag}-build.log"
    log.unlink(missing_ok=True)
    exports, env = build(head, tag, presets, args.skip_tests, log)
    os.environ["SOURCE_DATE_EPOCH"] = env["SOURCE_DATE_EPOCH"]
    originals = data_guard.load_original_hashes()
    packages = [release.pack(exports[preset], output, preset, tag, head) for preset in presets]
    # Re-scan the final folder (packages and kit) as a whole before anything is hashed for publication.
    guard = data_guard.scan([p for p in sorted(output.iterdir()) if p.is_file()], originals)
    guard.check()
    for preset in presets:
        (output / f"package-{preset}.json").unlink()
    date = datetime.datetime.fromtimestamp(int(env["SOURCE_DATE_EPOCH"]), datetime.UTC).date().isoformat()
    release.write_json(output / "release-manifest.json", {
        "schema_version": 1, "version": tag, "commit": head, "date": date, "phase": phase(version),
        "rehearsal": args.rehearsal, "packages": packages,
        "data_guard": {"result": "PASS", "entries": guard.scanned, "original_hashes": len(originals)}})
    notes = (ROOT / "docs/release/NOTES_TEMPLATE.md").read_text(encoding="utf-8").format(
        tag=tag, date=date, commit=head, phase=phase(version),
        summary=("**REHEARSAL BUILD – not a published release.**\n\n" if args.rehearsal else "") +
                f"OpenD2 {tag}: {phase(version)}.",
        changes=changelog_section(version),
        hash_note=f" and {len(originals)} original-file hashes" if originals else "; no original hash list was supplied",
        known_issues="", qa=f"Built locally with `eng/release-local.py`; tests {'skipped' if args.skip_tests else 'run'}"
                            f" (build log `{log.name}`).")
    (output / "RELEASE_NOTES.md").write_text(notes, encoding="utf-8")
    files = sorted(p for p in output.iterdir() if p.is_file() and p.name != "SHA256SUMS")
    (output / "SHA256SUMS").write_text("".join(f"{release.sha256(p)}  {p.name}\n" for p in files), encoding="utf-8")
    print((output / "SHA256SUMS").read_text(encoding="utf-8"), end="")
    print(f"LOCAL RELEASE OK {output}")


if __name__ == "__main__":
    main()
