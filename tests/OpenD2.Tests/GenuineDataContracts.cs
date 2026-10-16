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
		test("genuine: every DC6/DCC/COF/DT1/DS1/TXT/TBL/palette decodes except the one known-corrupt COF", () =>
		{
			using var mpq = new MpqAssetSource(data);
			var report = CorpusValidation.Run(mpq, kinds: k => k != "wav_pcm");
			var failures = report.Kinds.SelectMany(k => k.Failures).ToArray();
			// amblxbow.cof in genuine 1.11b does not have a COF header at all (byte 3 is not 20); the game never loads it.
			Check(failures.All(f => f.StartsWith("data\\global\\chars\\am\\cof\\amblxbow.cof:")) && failures.Length <= 1, "unexpected failures: " + string.Join(" | ", failures.Take(5)));
			Check(report.Kinds.Single(k => k.Kind == "dt1").UnreferencedFailures.All(f => f.Contains("Unsupported DT1 version")), "only old-version DT1 leftovers are unreferenced");
			Check(report.Skipped == 0 && report.Notes.Count == 0, "skipped: " + string.Join(" | ", report.Kinds.SelectMany(k => k.SkippedResources).Take(5)));
			Console.WriteLine($"  validated {report.Passed} resources; failed {report.Failed}; unreferenced {report.Unreferenced}; skipped {report.Skipped}");
		});
		test("genuine: every pal.pl2 and font TBL decodes; PL2 base palette matches pal.dat; glyph frames fit the font DC6", () =>
		{
			using var mpq = new MpqAssetSource(data);
			int pl2s = 0, fonts = 0;
			foreach (string name in mpq.List())
			{
				string? kind = AssetDecoders.Kind(name);
				if (kind == "pl2")
				{
					Check(mpq.TryRead(name, AssetDecoders.MaxInputBytes, out var raw), "read " + name);
					var pl2 = Pl2Palette.Parse(raw); pl2s++;
					Check(mpq.TryRead(name[..^3] + "dat", 768, out var dat), "read pal.dat next to " + name);
					for (int i = 0; i < 256; i++) Check(pl2.BaseRgb[i * 3] == dat[i * 3 + 2] && pl2.BaseRgb[i * 3 + 2] == dat[i * 3], "pl2 base vs pal.dat " + name);
				}
				else if (kind == "font_tbl")
				{
					Check(mpq.TryRead(name, AssetDecoders.MaxInputBytes, out var tbl), "read " + name);
					var font = FontTable.Parse(tbl); fonts++;
					Check(font.Glyphs.All(g => g.Height == font.LineHeight && g.Flags == 1), "glyph constants " + name);
					// Every font table has its DC6 next to it; a missing or unreadable DC6 fails the test.
					Check(mpq.TryRead(name[..^3] + "dc6", AssetDecoders.MaxInputBytes, out var dc6), "read dc6 for " + name);
					font.RequireFramesWithin(Dc6Image.Parse(dc6));
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
			test("genuine: semantic table schemas, BIN consistency and the M4-01 content inventory", () =>
		{
			using var mpq = new MpqAssetSource(data);
			byte[]? Read(string path) => mpq.TryRead(path, AssetDecoders.MaxInputBytes, out var bytes) ? bytes : null;
			foreach (var schema in TableSchemas.All)
			{
				var report = TableSchemas.Validate(ExcelTextTable.Parse(Read("data\\global\\excel\\" + schema.File) ?? throw new Exception("missing " + schema.File)), schema);
				Check(report.Valid && report.Records > 0, $"{schema.File}: {string.Join("; ", report.Errors)}");
			}
			int bins = 0;
			foreach (string path in mpq.List().Where(p => p.StartsWith("data\\global\\excel\\", StringComparison.Ordinal) && p.EndsWith(".bin", StringComparison.Ordinal)))
				if (Read(path[..^4] + ".txt") is { } txt)
				{
					var bin = TableSchemas.CheckBin(path, ExcelTextTable.Parse(txt), Read(path)!);
					Check(bin.Errors.Count == 0, $"{path}: {string.Join("; ", bin.Errors)}"); bins++;
				}
			var inv = ContentInventoryBuilder.Build(Read);
			var (raw, play) = (inv.Rows, inv.Playable);
			Check(bins > 50 && play.Skills == 210 && inv.SkillsByClass.Count == 7 && play.Quests == 27 && inv.QuestsByAct[4] == 3 && inv.AreasByAct.Count == 5, $"inventory {play}");
			Check(play.Monsters < raw.Monsters && play.BaseItems < raw.BaseItems && play.UniqueItems < raw.UniqueItems && play.Areas + inv.UberAreas == raw.Areas, "playable counts are filtered subsets");
			Console.WriteLine($"  {bins} BINs; rows {raw}; playable {play}; uber areas {inv.UberAreas}");
		});
		LegacyScreenContracts.Genuine(data, test);
}
}
