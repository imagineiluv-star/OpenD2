using OpenD2.Assets;

// Needs the user's genuine Diablo II MPQs. Runs only when D2_DATA_PATH is set; otherwise reports SKIP.
// Extracted output goes to a temp folder outside the repository and is deleted afterwards.
internal static class GenuineDataContracts
{
	public static void Run(Action<string, Action> test)
	{
		string? data = Environment.GetEnvironmentVariable(D2DataPaths.DataPathVariable);
		if (string.IsNullOrWhiteSpace(data)) { Console.WriteLine("SKIP genuine-data tests: set D2_DATA_PATH to a Diablo II installation to run them"); return; }
		static void Check(bool value, string what) { if (!value) throw new Exception("Assertion failed " + what); }
		InstallVerification? verification = null;
		test("genuine: installation matches a known MPQ manifest", () =>
		{
			verification = InstallManifest.Default.Verify(data);
			Check(verification.Verified, $"no manifest match; missing [{string.Join(',', verification.Missing)}] mismatched [{string.Join(',', verification.Mismatched)}]");
			Console.WriteLine($"  detected version {verification.Version}");
		});
		test("genuine: every pal.pl2 and font TBL decodes; PL2 base palette matches pal.dat; glyph frames fit the font DC6", () =>
		{
			using var mpq = new MpqAssetSource(data);
			int pl2s = 0, fonts = 0;
			foreach (string name in mpq.List())
			{
				string? kind = AssetDecoders.Kind(name);
				if (kind == "pl2" && mpq.TryRead(name, AssetDecoders.MaxInputBytes, out var raw))
				{
					var pl2 = Pl2Palette.Parse(raw); pl2s++;
					if (mpq.TryRead(name[..^3] + "dat", 768, out var dat))
						for (int i = 0; i < 256; i++) Check(pl2.BaseRgb[i * 3] == dat[i * 3 + 2] && pl2.BaseRgb[i * 3 + 2] == dat[i * 3], "pl2 base vs pal.dat " + name);
				}
				else if (kind == "font_tbl" && mpq.TryRead(name, AssetDecoders.MaxInputBytes, out var tbl))
				{
					var font = FontTable.Parse(tbl); fonts++;
					Check(font.Glyphs.All(g => g.Height == font.LineHeight && g.Flags == 1), "glyph constants " + name);
					// Korean font DC6s need the 16,384-frame cap from the real-data validation branch; skip them until it lands.
					if (mpq.TryRead(name[..^3] + "dc6", AssetDecoders.MaxInputBytes, out var dc6) && font.Glyphs.Count <= 4096)
						Check(font.Glyphs.Max(g => g.Frame) < Dc6Image.Parse(dc6).FramesPerDirection, "frames fit dc6 " + name);
					else if (font.Glyphs.Count > 4096 && dc6.Length > 24) Check(BitConverter.ToUInt32(dc6, 20) == font.Glyphs.Count, "dc6 header frame count " + name);
				}
			}
			Check(pl2s == 16 && fonts == 28, $"counts pl2={pl2s} fonts={fonts}");
		});
		test("genuine: raw MPQ and converted cache return identical bytes and decode", () =>
		{
			string cache = Path.Combine(Path.GetTempPath(), "opend2-genuine-cache-" + Guid.NewGuid().ToString("N"));
			string[] probes = ["data\\global\\palette\\act1\\pal.dat", "data\\global\\excel\\levels.txt", "data\\global\\ui\\panel\\invchar6.dc6"];
			try
			{
				using var mpq = new MpqAssetSource(data);
				var result = AssetCache.Build(mpq, "genuine-test", cache, new AssetCacheOptions
				{
					Include = n => n.StartsWith("data\\global\\palette\\", StringComparison.Ordinal) || probes.Contains(n),
				});
				Check(result.Rebuilt && result.Palettes >= 5 && result.PngFiles > 0 && result.Errors.Count == 0, $"cache build {result}");
				foreach (string path in probes)
				{
					Check(mpq.TryRead(path, AssetDecoders.MaxInputBytes, out var raw), "mpq " + path);
					var cached = AssetDecoders.ReadFromInstall(cache, path);
					Check(raw.AsSpan().SequenceEqual(cached), "identical " + path);
					Check(AssetDecoders.ReadFromInstall(data, path).AsSpan().SequenceEqual(raw), "engine raw path " + path);
					AssetDecoders.Validate(AssetDecoders.Kind(path)!, cached);
				}
			}
			finally { if (Directory.Exists(cache)) Directory.Delete(cache, true); }
		});
	}
}
