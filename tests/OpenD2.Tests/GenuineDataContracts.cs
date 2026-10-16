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
			Check(bins > 50 && inv.PlayerSkills == 210 && inv.SkillsByClass.Count == 7 && inv.Quests == 27 && inv.QuestsByAct[4] == 3 && inv.AreasByAct.Count == 5, $"inventory {inv.Skills}/{inv.Quests}/{inv.Areas}");
			Console.WriteLine($"  {bins} BINs; skills {inv.Skills} (player {inv.PlayerSkills}), monsters {inv.Monsters} (enabled {inv.EnabledMonsters}, superuniques {inv.SuperUniques}), base items {inv.BaseItems}, uniques {inv.UniqueItems}, sets {inv.SetItems}, runewords {inv.CompleteRunewords}/{inv.Runewords}, quests {inv.Quests}, areas {inv.Areas}");
		});
}
}
