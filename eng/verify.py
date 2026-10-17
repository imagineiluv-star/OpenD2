"""Repository boundary and accidental asset checks. Run from any directory."""
from pathlib import Path
import re
import subprocess
import xml.etree.ElementTree as ET

# C# comments, char literals and (verbatim/interpolated/raw) strings; prose may mention the engine, code may not.
CSHARP_NON_CODE = re.compile(
    r'//[^\n]*|/\*.*?\*/'
    r'|\$*"{3,}.*?"{3,}'                       # raw string literals
    r'|\$?@\$?"(?:[^"]|"")*"'                   # verbatim strings
    r'|\$?"(?:[^"\\\n]|\\.)*"'                 # regular / interpolated strings
    r"|'(?:[^'\\\n]|\\.)+'", re.S)


def csharp_code(text):
    return CSHARP_NON_CODE.sub(" ", text)


root = Path(__file__).resolve().parents[1]
for name in ("Core", "Assets", "Npc", "Online"):
    folder = root / "src" / ("OpenD2." + name)
    project = ET.parse(folder / ("OpenD2." + name + ".csproj")).getroot()
    assert project.attrib["Sdk"] == "Microsoft.NET.Sdk"
    assert not list(project.iter("PackageReference")), f"Review new {name} packages"
    refs = [x.attrib["Include"] for x in project.iter("ProjectReference")]
    assert refs == ([] if name == "Core" else ["../OpenD2.Core/OpenD2.Core.csproj"]), refs
    assert not list(project.iter("Reference")), "Direct DLL reference requires review"
    for source in folder.glob("*.cs"):
        assert not re.search(r"\bGodot\b", csharp_code(source.read_text(encoding="utf-8"))), \
            f"Engine dependency: {source}"
# Restrict only NEW migration trees; inherited C++ native binaries remain recorded debt.
paths = subprocess.check_output(["git", "ls-files", "--cached", "--others", "--exclude-standard"], cwd=root, text=True).splitlines()
for item in paths:
    if item.startswith(("src/", "tests/", "tools/", "eng/")):
        assert Path(item).suffix.lower() not in (".dll", ".exe", ".mpq", ".d2s", ".d2i", ".gguf"), item
print("PASS engine boundary and migration source-only checks")
