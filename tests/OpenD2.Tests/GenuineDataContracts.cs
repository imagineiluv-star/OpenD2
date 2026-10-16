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
