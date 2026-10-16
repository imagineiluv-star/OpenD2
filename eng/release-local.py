"""Build a release on this machine without GitHub Actions; never tags, pushes or uploads.

  python eng/release-local.py [--tag v0.1.0] [--preset Linux --preset Windows] [--skip-tests] [--rehearsal]

1. Preflight: clean work tree, the tag matches VERSION and, when the tag exists, HEAD is the tagged commit
   (--rehearsal allows an untagged HEAD and marks the notes as a rehearsal).
2. Exports HEAD (git archive) into a private build directory (mkdtemp under ~/.cache/opend2/release-build, guarded by
   0700 ownership checks; every run gets its own mkdtemp tree, so concurrent runs never share or delete each other's). Compiler paths are mapped
   (ContinuousIntegrationBuild/PathMap) so the directory name never reaches the outputs.
3. SOURCE_DATE_EPOCH = commit time; dotnet build/test (known failure allowlist) and Godot --export-release
   with VersionSuffix/SourceRevisionId/ContinuousIntegrationBuild so InformationalVersion is <version>+<sha>.
4. Packs with reproducible archives, runs the game-data guard (eng/data_guard.py; original hashes from
   OPEND2_ORIGINAL_SHA256SUMS or D2_DATA_PATH when set) and writes release-manifest.json, RELEASE_NOTES.md and
   SHA256SUMS into ../opend2-release/<tag>/ (outside the repository). Everything is written to a temporary sibling
   directory first and renamed into place only when every step passed; a failed run leaves nothing behind.
   A real release requires the original hash list; --rehearsal only warns without it.
5. The Godot editor and export template versions must match eng/toolchain.json and are recorded in the manifest.
Publishing stays a manual step for the maintainer: `git tag -a`, `gh release create`.
"""
import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile

sys.path.insert(0, str(Path(__file__).resolve().parent))
import data_guard  # noqa: E402
import release  # noqa: E402

ROOT = release.ROOT
CACHE_ROOT = Path(os.environ.get("XDG_CACHE_HOME") or Path.home() / ".cache") / "opend2" / "release-build"
TEMPLATE_FILES = {"Linux": ("linux_release.x86_64",), "Windows": ("windows_release_x86_64.exe",
                                                                     "windows_release_x86_64_console.exe"),
                  "macOS": ("macos.zip",)}
EXECUTABLES = {"Linux": "OpenD2.x86_64", "Windows": "OpenD2.exe", "macOS": "OpenD2.zip"}
# Tests that fail on a box without optional local tooling; anything else failing stops the release.
KNOWN_FAILURES = ("NPC bundled native runtime executes",)
PHASES = {(0, 1): "P1 original data + P2-1 foundation", (0, 2): "P2 original screens", (0, 3): "P3 rules and skills",
          (0, 4): "P4 levels and .d2s"}


def git(*args, cwd=ROOT):
    return subprocess.run(["git", *args], cwd=cwd, check=True, capture_output=True, text=True,
                          encoding="utf-8").stdout.strip()


def phase(version):
    major, minor = (int(x) for x in version.split(".")[:2])
    if major >= 1:
        return "1.14d parity" if (major, minor) == (1, 0) else "P6"
    return PHASES.get((major, minor), "P5 verification and 1.14d parity" if 5 <= minor <= 9 else "unplanned")


def preflight(tag, rehearsal):
    release.check_tag_matches_version(tag)
    if subprocess.run([sys.executable, str(ROOT / "eng/version.py"), "check"], cwd=ROOT).returncode:
        raise SystemExit("VERSION and src/OpenD2.Client/project.godot disagree; run eng/version.py sync")
    if git("status", "--porcelain", "--untracked-files=no"):
        raise SystemExit("Work tree has uncommitted changes")
    head = git("rev-parse", "HEAD")
    tagged = subprocess.run(["git", "rev-parse", "-q", "--verify", f"refs/tags/{tag}^{{commit}}"], cwd=ROOT,
                            capture_output=True, text=True, encoding="utf-8").stdout.strip()
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


def run(command, cwd, env, log, capture=None):
    """Append the command and its output to log; with capture, also write this step alone to that file."""
    with log.open("a", encoding="utf-8") as stream:
        stream.write(f"$ {' '.join(map(str, command))}\n")
        stream.flush()
        result = subprocess.run(command, cwd=cwd, env=env, capture_output=True, text=True, encoding="utf-8",
                                errors="replace")
        stream.write(result.stdout + result.stderr)
    if capture:
        capture.write_text(result.stdout + result.stderr, encoding="utf-8")
    return result.returncode


def tests_acceptable(code, text):
    """Exit 0, or only allow-listed FAIL lines plus a matching "<passed>/<total> passed" summary."""
    summary = re.search(r"^(\d+)/(\d+) passed", text, re.M)
    if not summary:
        return False, "no test summary line (crashed?)"
    passed, total = map(int, summary.groups())
    failures = re.findall(r"^FAIL\b[^\n]*", text, re.M)
    unexpected = [line for line in failures if not any(line.startswith(f"FAIL {known}") for known in KNOWN_FAILURES)]
    if code == 0 and passed == total:
        return True, "all passed"
    if unexpected or len(failures) != total - passed or not failures:
        return False, f"{total - passed} failed, {len(unexpected)} unexpected"
    return True, f"{passed}/{total}, only known failures"


def sha256_file(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def godot_toolchain(godot, presets, env):
    """Check the editor and export templates against eng/toolchain.json; return what the manifest records."""
    pinned = json.loads((ROOT / "eng/toolchain.json").read_text(encoding="utf-8"))
    version = subprocess.run([godot, "--version"], capture_output=True, text=True, encoding="utf-8",
                             env=env).stdout.strip().splitlines()[-1]
    if not version.startswith(pinned["templateVersion"] + "."):
        raise SystemExit(f"Godot {version} does not match pinned {pinned['templateVersion']}")
    data_home = Path(env.get("XDG_DATA_HOME") or Path.home() / ".local/share")
    templates = data_home / "godot/export_templates" / pinned["templateVersion"]
    if (templates / "version.txt").read_text(encoding="utf-8").strip() != pinned["templateVersion"]:
        raise SystemExit(f"Export templates in {templates} are not {pinned['templateVersion']}")
    hashes = {}
    for preset in presets:
        for name in TEMPLATE_FILES[preset]:
            hashes[name] = sha256_file(templates / name)
    return {"godot": version, "templates": pinned["templateVersion"], "template_sha256": hashes,
            "dotnet": pinned["dotnet"], "python": sys.version.split()[0]}


def build(build_root, head, tag, presets, skip_tests, log):
    source = build_root / "src"
    source.mkdir()
    archive = subprocess.Popen(["git", "archive", "--format=tar", head], cwd=ROOT, stdout=subprocess.PIPE)
    extract = subprocess.run(["tar", "-x", "-C", str(source)], stdin=archive.stdout)
    archive.stdout.close()
    if archive.wait() or extract.returncode:
        raise SystemExit("git archive | tar failed")
    tools = ROOT / ".local-tools"
    # Copy (not symlink) only the prebuilt runtimes Directory.Build.targets packages, so nothing else leaks in.
    for runtime in ("native/dist", "llama/dist"):
        if (tools / runtime).is_dir():
            shutil.copytree(tools / runtime, source / ".local-tools" / runtime)
    epoch = git("show", "-s", "--format=%ct", head)
    suffix = tag.partition("-")[2]
    env = dict(os.environ, SOURCE_DATE_EPOCH=epoch, SourceRevisionId=head, VersionSuffix=suffix,
               ContinuousIntegrationBuild="true", PathMap=f"{source}=/_/", DOTNET_CLI_TELEMETRY_OPTOUT="1",
               DOTNET_NOLOGO="1", LC_ALL="C.UTF-8", LANG="C.UTF-8", TZ="UTC")
    godot = (tools / "godot-path.txt").read_text(encoding="utf-8").strip()
    toolchain = godot_toolchain(godot, presets, env)
    if run(["dotnet", "build", "OpenD2.sln", "-c", "Release", "-m:1", "-p:BuildInParallel=false"], source, env, log):
        raise SystemExit(f"dotnet build failed; see {log}")
    if not skip_tests:
        test_log = build_root / "tests.log"
        code = run(["dotnet", "run", "--project", "tests/OpenD2.Tests", "-c", "Release", "--no-build"], source, env,
                   log, capture=test_log)
        ok, detail = tests_acceptable(code, test_log.read_text(encoding="utf-8"))
        if not ok:
            raise SystemExit(f"Tests failed ({detail}); see {log}")
    exports = {}
    for preset in presets:
        destination = build_root / "export" / preset / EXECUTABLES[preset]
        destination.parent.mkdir(parents=True)
        if run([godot, "--headless", "--path", "src/OpenD2.Client", "--export-release", preset, str(destination)],
               source, env, log) or not destination.is_file():
            raise SystemExit(f"Godot export {preset} failed; see {log}")
        exports[preset] = destination.parent
    return exports, env, toolchain


class BuildDirectory:
    """A private, uniquely named mkdtemp tree (mode 0700) under the user's cache, removed afterwards."""

    def __enter__(self):
        CACHE_ROOT.mkdir(parents=True, exist_ok=True, mode=0o700)
        if CACHE_ROOT.is_symlink() or CACHE_ROOT.stat().st_uid != os.getuid():
            raise SystemExit(f"{CACHE_ROOT} is not a private directory owned by you")
        self.path = Path(tempfile.mkdtemp(prefix="build-", dir=CACHE_ROOT))
        return self.path

    def __exit__(self, *_):
        shutil.rmtree(self.path, ignore_errors=True)


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
    output.parent.mkdir(parents=True, exist_ok=True)
    log = output.parent / f"{tag}-build.log"
    log.unlink(missing_ok=True)
    os.umask(0o022)
    final = output
    output = Path(tempfile.mkdtemp(prefix=f".{tag}.partial-", dir=final.parent))
    try:
        with BuildDirectory() as build_root:
            assemble(args, version, tag, presets, head, output, log, build_root)
        if final.exists():
            raise SystemExit(f"{final} appeared during the build; result left in {output}")
        output.chmod(0o755)
        os.rename(output, final)
    except BaseException:
        shutil.rmtree(output, ignore_errors=True)
        raise
    print((final / "SHA256SUMS").read_text(encoding="utf-8"), end="")
    print(f"LOCAL RELEASE OK {final}")


def assemble(args, version, tag, presets, head, output, log, build_root):
    exports, env, toolchain = build(build_root, head, tag, presets, args.skip_tests, log)
    os.environ["SOURCE_DATE_EPOCH"] = env["SOURCE_DATE_EPOCH"]
    originals = data_guard.load_original_hashes()
    packages = [release.pack(exports[preset], output, preset, tag, head, rehearsal=args.rehearsal) for preset in presets]
    # Re-scan the final folder (packages and kit) as a whole before anything is hashed for publication.
    guard = data_guard.scan([p for p in sorted(output.iterdir()) if p.is_file()], originals,
                            require_original_hashes=not args.rehearsal)
    guard.check()
    for preset in presets:
        (output / f"package-{preset}.json").unlink()
    date = datetime.datetime.fromtimestamp(int(env["SOURCE_DATE_EPOCH"]), datetime.UTC).date().isoformat()
    release.write_json(output / "release-manifest.json", {
        "schema_version": 1, "version": tag, "commit": head, "date": date, "phase": phase(version),
        "rehearsal": args.rehearsal, "toolchain": toolchain, "packages": packages,
        "data_guard": {"result": guard.result, "warnings": guard.warnings, "entries": guard.scanned, "original_hashes": len(originals)}})
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


if __name__ == "__main__":
    main()
