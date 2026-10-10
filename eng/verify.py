"""Repository boundary and accidental asset checks. Run from any directory."""
from pathlib import Path
import subprocess
import xml.etree.ElementTree as ET

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
        assert "Godot" not in source.read_text(encoding="utf-8"), f"Engine dependency: {source}"
# Restrict only NEW migration trees; inherited C++ native binaries remain recorded debt.
paths = subprocess.check_output(["git", "ls-files", "--cached", "--others", "--exclude-standard"], cwd=root, text=True).splitlines()
for item in paths:
    if item.startswith(("src/", "tests/", "tools/", "eng/")):
        assert Path(item).suffix.lower() not in (".dll", ".exe", ".mpq", ".d2s", ".d2i", ".gguf"), item
print("PASS engine boundary and migration source-only checks")
