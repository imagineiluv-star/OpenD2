"""Keep derived version fields in sync with the root VERSION file (the single source).

  python eng/version.py show          print VERSION
  python eng/version.py check         fail if project.godot config/version differs
  python eng/version.py sync          rewrite project.godot config/version from VERSION
"""
from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[1]
GODOT_PROJECT = ROOT / "src/OpenD2.Client/project.godot"
sys.path.insert(0, str(Path(__file__).resolve().parent))
from release import read_version  # noqa: E402

PATTERN = re.compile(r'^config/version="([^"\n]*)"$', re.M)


def godot_version(path=GODOT_PROJECT):
    match = PATTERN.search(path.read_text(encoding="utf-8"))
    return match.group(1) if match else None


def sync(path=GODOT_PROJECT, root=ROOT):
    version, text = read_version(root), path.read_text(encoding="utf-8")
    if PATTERN.search(text):
        text = PATTERN.sub(f'config/version="{version}"', text, count=1)
    else:
        text = text.replace('[application]\n', f'[application]\nconfig/version="{version}"\n', 1)
    path.write_text(text, encoding="utf-8")
    return version


def main():
    command = sys.argv[1] if len(sys.argv) > 1 else "check"
    version = read_version()
    if command == "show":
        print(version)
    elif command == "sync":
        print(f"VERSION SYNC OK {sync()}")
    elif command == "check":
        found = godot_version()
        if found != version:
            sys.exit(f"project.godot config/version {found!r} != VERSION {version!r}; run eng/version.py sync")
        print(f"VERSION CHECK OK {version}")
    else:
        sys.exit(__doc__)


if __name__ == "__main__":
    main()
