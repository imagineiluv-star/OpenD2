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
        'var t = Type.GetType("Godot.Node, GodotSharp");', 'var t = Type.GetType("global::Godot.Node");',
    )
    ALLOWED = (
        "// the Godot client calls this", "/* Godot */ int a;", "/// <summary>Godot host</summary>",
        'var s = "the Godot client";', 'var s = @"Godot ""quoted"" text";', 'var s = $"Godot {count} frames";',
        'var s = $"{{count}} Godot frames";', 'var s = """\n  made for the Godot client\n  """;',
        'var s = $$"""{literal} {{count}} Godot""";', "char c = '\\''; int GodotLike = 1;", 'var e = "";',
        'var n = nameof(GodotPath);',
    )

    def test_engine_code_is_caught(self):
        for source in self.CAUGHT:
            with self.subTest(source=source):
                self.assertTrue(engine_references(source), split(source))

    def test_prose_is_allowed(self):
        for source in self.ALLOWED:
            with self.subTest(source=source):
                self.assertEqual([], engine_references(source), split(source))


if __name__ == "__main__":
    unittest.main()
