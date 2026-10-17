using System.Security.Cryptography;
using OpenD2.Assets;

// P2-2: per-screen/per-act palette rules, PL2 light and blend compositing, gamma. Synthetic fixtures only;
// genuine checks run from GenuineDataContracts when D2_DATA_PATH is set and store only hashes.
internal static class ScreenPaletteContracts
{
	private static void Check(bool value, string what) { if (!value) throw new Exception("Assertion failed " + what); }
	private const int Alpha25 = 1024 + 32 * 256 + 16 * 256 + 256, Additive = Alpha25 + 3 * 65536, Multiplicative = Additive + 65536;

	/// <summary>Synthetic PL2 whose light and blend tables are seeded noise, so any wrong table or argument order shows.</summary>
	internal static byte[] NoisyPl2(int seed = 11)
	{
		var data = Pl2FontContracts.Pl2(); var rng = new Random(seed);
		rng.NextBytes(data.AsSpan(1024, 32 * 256)); rng.NextBytes(data.AsSpan(Alpha25, 5 * 65536));
		return data;
	}
	static Dc6Frame Frame(int w, int h, int seed)
	{
		var rng = new Random(seed); var idx = new byte[w * h]; rng.NextBytes(idx);
		var op = new byte[w * h]; for (int i = 0; i < op.Length; i++) op[i] = (byte)(i % 5 == 0 ? 0 : 255);
		return new(w, h, 0, 0, idx, op);
	}

	public static void Run(Action<string, Action> test)
	{
		test("screen palettes: screens follow the original PAL_* rules, the world uses act1..act5, auto resolves per screen", () =>
		{
			Check(ScreenPalettes.ForScene("gameselect") == "units" && ScreenPalettes.ForScene("charselect") == "fechar" && ScreenPalettes.ForScene("trademark") == "sky"
				&& ScreenPalettes.ForScene("loading") == "loading" && ScreenPalettes.ForScene("charlist") == "sky", "screen rules");
			Check(Enumerable.Range(1, 5).Select(ScreenPalettes.ForAct).SequenceEqual(["act1", "act2", "act3", "act4", "act5"]), "acts");
			foreach (int bad in new[] { 0, 6 }) try { ScreenPalettes.ForAct(bad); throw new Exception("bad act"); } catch (ArgumentOutOfRangeException) { }
			Check(ScreenPalettes.Screens.Values.All(ScreenPalettes.Names.Contains), "every rule names a known palette");
			Check(ScreenPalettes.Resolve(null, "charselect") == "fechar" && ScreenPalettes.Resolve("AUTO", "gameselect") == "units" && ScreenPalettes.Resolve("Act3", "gameselect") == "act3", "resolve");
			foreach (var bad in new[] { "..\\x", "act6", "unit s" }) try { ScreenPalettes.Resolve(bad, "gameselect"); throw new Exception("bad palette " + bad); } catch (ArgumentException) { }
			try { ScreenPalettes.ForScene("nope"); throw new Exception("unknown screen guessed"); } catch (ArgumentException) { }
			Check(ScreenPalettes.ForAssetPath("data\\global\\tiles\\act2\\town\\floor.dt1") == "act2" && ScreenPalettes.ForAssetPath("data\\global\\monsters\\act5\\x.dc6") == "act5"
				&& ScreenPalettes.ForAssetPath("data\\global\\ui\\frontend\\charactercreationscreenexp.dc6") == "fechar"
				&& ScreenPalettes.ForAssetPath("data\\global\\ui\\frontend\\trademarkscreenexp.dc6") == "sky"
				&& ScreenPalettes.ForAssetPath("data\\global\\ui\\loading\\loadingscreen.dc6") == "loading"
				&& ScreenPalettes.ForAssetPath("data\\global\\ui\\frontend\\gameselectscreenexp.dc6") == "units"
				&& ScreenPalettes.ForAssetPath("data\\global\\items\\invsword.dc6") == "units" && ScreenPalettes.ForAssetPath("data\\global\\act10\\x.dc6") == "units", "asset path rules");
		});
		test("PL2 compositing: light then 25/50/75/additive/multiplicative blend are pixel-equal to direct table lookups", () =>
		{
			var raw = NoisyPl2(); var pl2 = Pl2Palette.Parse(raw);
			var back = Frame(24, 16, 1); var sprite = Frame(24, 16, 2);
			var modes = new (LegacyBlendMode Mode, int Table)[] { (LegacyBlendMode.Trans25, Alpha25), (LegacyBlendMode.Trans50, Alpha25 + 65536), (LegacyBlendMode.Trans75, Alpha25 + 2 * 65536),
				(LegacyBlendMode.Additive, Additive), (LegacyBlendMode.Multiplicative, Multiplicative) };
			foreach (int? light in new int?[] { null, 0, 17, 31 })
				foreach (var (mode, table) in modes.Prepend((LegacyBlendMode.Opaque, -1)))
				{
					var fb = new LegacyFrameBuffer(LegacyResolution.R640x480);
					fb.Draw(back, 0, 0); fb.Draw(sprite, 4, 2, new LegacyDrawEffect(mode, light), pl2);
					for (int y = 0; y < 20; y++)
						for (int x = 0; x < 30; x++)
						{
							int bx = x, by = y, sx = x - 4, sy = y - 2; int? dst = null;
							if (bx < 24 && by < 16 && back.Opacity[by * 24 + bx] != 0) dst = back.Indices[by * 24 + bx];
							int? expected = dst;
							if (sx is >= 0 and < 24 && sy is >= 0 and < 16 && sprite.Opacity[sy * 24 + sx] != 0)
							{
								int s = sprite.Indices[sy * 24 + sx]; if (light is { } l) s = raw[1024 + l * 256 + s];
								expected = table < 0 ? s : raw[table + s * 256 + (dst ?? 0)];
							}
							int o = y * 640 + x;
							Check(expected is null ? fb.Coverage[o] == 0 : fb.Coverage[o] == 255 && fb.Indices[o] == expected, $"{mode} light {light} at {x},{y}");
						}
				}
			var whole = new LegacyFrameBuffer(LegacyResolution.R640x480); whole.Draw(back, 0, 0); whole.ApplyLight(pl2, 9);
			Check(Enumerable.Range(0, 24 * 16).All(i => back.Opacity[i] == 0 ? whole.Coverage[i / 24 * 640 + i % 24] == 0 : whole.Indices[i / 24 * 640 + i % 24] == raw[1024 + 9 * 256 + back.Indices[i]]), "whole-screen light");
			try { new LegacyFrameBuffer(LegacyResolution.R640x480).Draw(sprite, 0, 0, new(LegacyBlendMode.Trans50), null); throw new Exception("blend without pl2"); } catch (ArgumentException) { }
			try { new LegacyFrameBuffer(LegacyResolution.R640x480).Draw(sprite, 0, 0, new(Light: 32), pl2); throw new Exception("light 32"); } catch (ArgumentOutOfRangeException) { }
			Check(LegacyDrawEffect.Parse("50") == LegacyBlendMode.Trans50 && LegacyDrawEffect.Parse("Additive") == LegacyBlendMode.Additive, "blend parse");
		});
		test("gamma: 1.0 is the identity and leaves captures unchanged; other values follow the power ramp", () =>
		{
			Check(LegacyGamma.Table(1.0).Select((v, i) => v == i).All(b => b), "identity");
			var g2 = LegacyGamma.Table(2.0); Check(g2[0] == 0 && g2[255] == 255 && g2[64] == (byte)Math.Round(255 * Math.Sqrt(64 / 255.0)), "gamma 2");
			Check(LegacyGamma.Table(0.5)[128] < 128, "darker below 1");
			foreach (double bad in new[] { 0.4, 3.1, double.NaN }) try { LegacyGamma.Table(bad); throw new Exception("bad gamma"); } catch (ArgumentOutOfRangeException) { }
			Check(LegacyRenderOptions.ParseGamma(null) == 1.0 && LegacyRenderOptions.ParseGamma("1.5") == 1.5 && LegacyRenderOptions.ParseLight(null) is null && LegacyRenderOptions.ParseLight("31") == 31, "options");
			foreach (var bad in new[] { "32", "-1", "x" }) try { LegacyRenderOptions.ParseLight(bad); throw new Exception("bad light"); } catch (ArgumentException) { }
			var files = new Dictionary<string, byte[]>
			{
				[ScreenPalettes.DatPath("units")] = MpqPipelineContracts.PaletteBytes(),
				[ScreenPalettes.DatPath("fechar")] = MpqPipelineContracts.PaletteBytes().Select(b => (byte)(b ^ 0x55)).ToArray(),
				[ScreenPalettes.Pl2Path("units")] = NoisyPl2(),
				[LegacyScreenRenderer.Scenes["gameselect"]] = LegacyFormatContracts.Dc6(),
				[LegacyScreenRenderer.Scenes["charselect"]] = LegacyFormatContracts.Dc6(),
			};
			byte[] Read(string p) => files.TryGetValue(p, out var b) ? b : throw new FileNotFoundException(p);
			var req = new LegacyCaptureRequest("gameselect", LegacyResolution.R800x600);
			var plain = LegacyScreenRenderer.Render(req, Read);
			Check(plain.Rgb.AsSpan().SequenceEqual(LegacyScreenRenderer.Render(req with { Palette = "units", Gamma = 1.0 }, Read).Rgb), "auto = units, gamma 1 unchanged");
			var bright = LegacyScreenRenderer.Render(req with { Gamma = 2.0 }, Read);
			Check(Enumerable.Range(0, plain.Rgb.Length).All(i => bright.Rgb[i] == g2[plain.Rgb[i]]), "gamma ramp on final RGB");
			var lit = LegacyScreenRenderer.Render(req with { Light = 5 }, Read);
			Check(lit.Sources.Contains(ScreenPalettes.Pl2Path("units")) && !lit.Rgb.AsSpan().SequenceEqual(plain.Rgb), "light reads pal.pl2");
			var charSelect = LegacyScreenRenderer.Render(req with { Scene = "charselect" }, Read);
			Check(charSelect.Sources.Contains(ScreenPalettes.DatPath("fechar")) && !charSelect.Rgb.AsSpan().SequenceEqual(plain.Rgb), "charselect auto uses fechar");
		});
	}

	// Independent Python reference (own DC6 decoder, raw pal.dat BGR, raw PL2 light offset 1024 + level*256), 1.11b data.
	public const string CharSelectFecharRgbSha256 = "aec740fbee30445062193e0bed896ae9b28b4808d0fdc55bc9690f7e330d8a8f";
	public const string CharSelectUnitsRgbSha256 = "c85bab3e2a8831ef0678ddc14460a50d8703eecb4630a84abfc14a59288b69f5";
	public const string GameSelectAct1Light12RgbSha256 = "47be175a9669227e8649db4b21bd11a68e1b891b150748f7a11aae62a29e30f2";

	public static void Genuine(string data, Action<string, Action> test)
	{
		test("genuine: charselect auto palette is fechar and matches the Python reference 100%; act1 light 12 matches the raw PL2 table", () =>
		{
			using var mpq = new MpqAssetSource(data);
			byte[] Read(string p) => mpq.TryRead(p, AssetDecoders.MaxInputBytes, out var b) ? b : throw new FileNotFoundException(p);
			string Hash(LegacyCapture c) => Convert.ToHexStringLower(SHA256.HashData(c.Rgb));
			var req = new LegacyCaptureRequest("charselect", LegacyResolution.R800x600);
			var fechar = LegacyScreenRenderer.Render(req, Read);
			Check(Hash(fechar) == CharSelectFecharRgbSha256, $"charselect fechar {Hash(fechar)}");
			Check(Hash(LegacyScreenRenderer.Render(req with { Palette = "units" }, Read)) == CharSelectUnitsRgbSha256, "charselect units (previous default)");
			Check(Hash(LegacyScreenRenderer.Render(new LegacyCaptureRequest("gameselect", LegacyResolution.R800x600), Read)) == LegacyScreenContracts.GameSelectUnitsRgbSha256, "gameselect auto unchanged");
			var lit = LegacyScreenRenderer.Render(new LegacyCaptureRequest("gameselect", LegacyResolution.R800x600, "act1", Light: 12), Read);
			Check(Hash(lit) == GameSelectAct1Light12RgbSha256, $"act1 light 12 {Hash(lit)}");
			Check(Hash(LegacyScreenRenderer.Render(new LegacyCaptureRequest("gameselect", LegacyResolution.R800x600, "act1", Light: 12), Read)) == Hash(lit), "deterministic");
		});
		test("genuine: act1 PL2 blend of a UI sprite over the game-select background equals direct table lookups", () =>
		{
			using var mpq = new MpqAssetSource(data);
			byte[] Read(string p) => mpq.TryRead(p, AssetDecoders.MaxInputBytes, out var b) ? b : throw new FileNotFoundException(p);
			var raw = Read(ScreenPalettes.Pl2Path("act1")); var pl2 = Pl2Palette.Parse(raw);
			var bg = Dc6Image.Parse(Read(LegacyScreenRenderer.Scenes["gameselect"])); var sprite = Dc6Image.Parse(Read(LegacyScreenRenderer.Scenes["charselect"])).DecodeFrame(0, 0);
			var baseFb = new LegacyFrameBuffer(LegacyResolution.R800x600); baseFb.DrawTiledBackground(bg);
			foreach (var (mode, table) in new[] { (LegacyBlendMode.Trans25, Alpha25), (LegacyBlendMode.Trans50, Alpha25 + 65536), (LegacyBlendMode.Trans75, Alpha25 + 131072), (LegacyBlendMode.Additive, Additive), (LegacyBlendMode.Multiplicative, Multiplicative) })
			{
				var fb = new LegacyFrameBuffer(LegacyResolution.R800x600); fb.DrawTiledBackground(bg);
				fb.Draw(sprite, 100, 50, new LegacyDrawEffect(mode, 20), pl2);
				long changed = 0;
				for (int y = 0; y < sprite.Height; y++)
					for (int x = 0; x < sprite.Width; x++)
					{
						int sx = 100 + x, sy = 50 + y; if (sx >= 800 || sy >= 600) continue;
						int o = sy * 800 + sx, s = y * sprite.Width + x;
						int dst = baseFb.Coverage[o] != 0 ? baseFb.Indices[o] : 0;
						int expected = sprite.Opacity[s] == 0 ? baseFb.Indices[o] : raw[table + raw[1024 + 20 * 256 + sprite.Indices[s]] * 256 + dst];
						Check(fb.Indices[o] == expected, $"{mode} at {sx},{sy}"); if (expected != dst) changed++;
					}
				Check(changed > 1000, $"{mode} changes pixels ({changed})");
			}
		});
	}
}
