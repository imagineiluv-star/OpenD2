"""Repeat cold import and warm export teardown; fail at the first error, never retry.

Uses a tiny desktop-only project without C# or templates to isolate engine lifetime.
Actual .NET release exports and extracted-package execution remain separate CI gates.
--godot can point to an older binary for a strict baseline reproduction.
"""
import argparse
from pathlib import Path
import shutil
import tempfile

from godot_process import run_godot


ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--godot", help="Editor executable; defaults to bootstrap output")
    parser.add_argument("--iterations", type=int, default=12)
    parser.add_argument("--logs", type=Path, default=ROOT / "artifacts/validation/lifecycle")
    args = parser.parse_args()
    if not 1 <= args.iterations <= 100:
        parser.error("--iterations must be between 1 and 100")
    godot = args.godot or (ROOT / ".local-tools/godot-path.txt").read_text(encoding="utf-8").strip()
    run_godot([godot, "--version"], cwd=ROOT, log=args.logs / "version.log", timeout=30)
    with tempfile.TemporaryDirectory(prefix="opend2-editor-lifecycle-") as temporary:
        project = Path(temporary) / "project"
        project.mkdir()
        (project / "project.godot").write_text(
            'config_version=5\n[application]\nconfig/name="Editor lifecycle probe"\n'
            '[rendering]\nrenderer/rendering_method="gl_compatibility"\n', encoding="utf-8")
        # --export-pack exercises export/automatic quit without compiling C# or using templates.
        (project / "export_presets.cfg").write_text(
            '[preset.0]\nname="Probe"\nplatform="Linux"\nrunnable=true\n'
            'export_filter="all_resources"\ninclude_filter=""\nexclude_filter=""\n'
            '[preset.0.options]\nbinary_format/architecture="x86_64"\n', encoding="utf-8")
        (project / "probe.gd").write_text('extends Node\n', encoding="utf-8")
        for index in range(1, args.iterations + 1):
            if (project / ".godot").exists():
                shutil.rmtree(project / ".godot")
            # Vary teardown timing around the old Android poll interval (3 seconds).
            frames = (1, 30, 120, 180)[(index - 1) % 4]
            run_godot([godot, "--headless", "--path", str(project), "--import",
                       "--quit-after", str(frames), "--max-fps", "60"], cwd=ROOT,
                      log=args.logs / f"{index:02}-import.log", timeout=60)
            destination = Path(temporary) / "probe.pck"
            destination.unlink(missing_ok=True)
            run_godot([godot, "--headless", "--path", str(project), "--export-pack",
                       "Probe", str(destination)], cwd=ROOT,
                      log=args.logs / f"{index:02}-export.log", timeout=60)
            if not destination.is_file() or destination.stat().st_size == 0:
                raise SystemExit("Lifecycle probe export is missing or empty")
    print(f"EDITOR LIFECYCLE PASS: {args.iterations} cold imports + {args.iterations} exports")


if __name__ == "__main__":
    main()
