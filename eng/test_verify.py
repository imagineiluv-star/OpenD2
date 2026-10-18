"""eng/verify.py engine-boundary lexer: prose may mention the engine, code (incl. interpolation holes) may not."""
import unittest

from csharp_lex import engine_references, split


class EngineBoundaryTests(unittest.TestCase):
    CAUGHT = (
        "using Godot;", "using G = Godot;", "var n = global::Godot.Node;", "[Godot.GlobalClass] class A {}",
        "char q = '\"'; Godot.Node n; char r = '\"';", 'var s = "/*"; Godot.Node n; var t = "*/";',
        'var s = @"\\"; Godot.Node n;', 'var u = "http://x"; Godot.Node n;',
        'var s = $"{Godot.Engine.GetFramesDrawn()}";', 'var s = $@"{Godot.Engine.GetFramesDrawn()}";',
        'var s = @$"x {nameof(Godot.Node)} y";', 'var s = $"{nameof(Godot.Node)}";',
        'var s = $"""\n  {Godot.Engine.GetFramesDrawn()}\n  """;', 'var s = $$"""{{Godot.OS.GetName()}}""";',
        'var s = $"a {(x ? "b" : $"{Godot.OS.GetName()}")} c";', "\\u0047odot.Node n;",
        'var t = Type.GetType("Godot.Node, GodotSharp");', '#region /*\nGodot.Node n;\n#endregion */',
        "#region don't\nGodot.Node n;\n#endregion", '#region /*\rGodot.Node n;\r#endregion */', '// x\rGodot.Node n;',
        '#region /*\u2028Godot.Node n;\u2028#endregion */', '// x\u2029Godot.Node n;', '// x\u0085Godot.Node n;',
        '// x\r\nGodot.Node n;', '#region @"\nGodot.Node n;\n#endregion', 'var t = Type.GetType("global::Godot.Node");',
    )
    ALLOWED = (
        "// the Godot client calls this", "/* Godot */ int a;", "/// <summary>Godot host</summary>",
        'var s = "the Godot client";', 'var s = @"Godot ""quoted"" text";', 'var s = $"Godot {count} frames";',
        'var s = $"{{count}} Godot frames";', 'var s = """\n  made for the Godot client\n  """;',
        'var s = $$"""{literal} {{count}} Godot""";', "char c = '\\''; int GodotLike = 1;", 'var e = "";',
        'var n = nameof(GodotPath);', '#region the Godot client\nint a;\n#endregion', '#if DEBUG\nint b;\n#endif',
    )

    def test_engine_code_is_caught(self):
        for source in self.CAUGHT:
            with self.subTest(source=source):
                self.assertTrue(engine_references(source), split(source))

    def test_prose_is_allowed(self):
        for source in self.ALLOWED:
            with self.subTest(source=source):
                self.assertEqual([], engine_references(source), split(source))


class VerifyScriptTests(unittest.TestCase):
    def test_nested_bin_and_obj_folders_are_scanned(self):
        """The SDK excludes only <project>/bin and <project>/obj; Feature/bin/X.cs still compiles, so verify reads it."""
        import shutil
        import subprocess
        import sys
        import tempfile
        from pathlib import Path
        repo = Path(__file__).resolve().parents[1]
        with tempfile.TemporaryDirectory() as temporary:
            clone = Path(temporary) / "repo"
            shutil.copytree(repo / "eng", clone / "eng", ignore=shutil.ignore_patterns("__pycache__"))
            for name in ("Core", "Assets", "Npc", "Online"):
                project = repo / "src" / f"OpenD2.{name}" / f"OpenD2.{name}.csproj"
                (clone / "src" / f"OpenD2.{name}").mkdir(parents=True)
                shutil.copy(project, clone / "src" / f"OpenD2.{name}")
            subprocess.run(["git", "init", "-q"], cwd=clone, check=True)
            core = clone / "src" / "OpenD2.Core"
            (core / "obj").mkdir()
            (core / "obj" / "Generated.cs").write_text("using Godot;")  # top-level obj: not compiled, skipped
            ok = subprocess.run([sys.executable, "eng/verify.py"], cwd=clone, capture_output=True, text=True)
            self.assertEqual(0, ok.returncode, ok.stderr)
            (core / "Feature" / "bin").mkdir(parents=True)
            (core / "Feature" / "bin" / "X.cs").write_text("class X { Godot.Node n; }")
            bad = subprocess.run([sys.executable, "eng/verify.py"], cwd=clone, capture_output=True, text=True)
            self.assertNotEqual(0, bad.returncode)
            self.assertIn("Feature/bin/X.cs".replace("/", __import__("os").sep), bad.stderr)


if __name__ == "__main__":
    unittest.main()
