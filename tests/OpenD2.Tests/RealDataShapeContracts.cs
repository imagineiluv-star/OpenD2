using System.Buffers.Binary;
using System.Text;
using OpenD2.Assets;

// Synthetic reproductions of the shapes found while validating genuine 1.11b files. No game data.
internal static class RealDataShapeContracts
{
	// Simulates an over-budget read (big.dc6) and a decoder-unrelated exception type (boom.dc6).
	private sealed class FaultySource(Dictionary<string, byte[]> files) : IAssetSource
	{
		public IReadOnlyList<string> List() => files.Keys.Order(StringComparer.Ordinal).ToArray();
		public bool TryRead(string path, long maxBytes, out byte[] data)
		{
			if (path.EndsWith("boom.dc6", StringComparison.Ordinal)) throw new NotSupportedException("simulated");
			data = path.EndsWith("big.dc6", StringComparison.Ordinal) ? [] : files[path];
			return !path.EndsWith("big.dc6", StringComparison.Ordinal);
		}
		public void Dispose() { }
	}
	private sealed class MemorySource(Dictionary<string, byte[]> files) : IAssetSource
	{
		public IReadOnlyList<string> List() => files.Keys.Order(StringComparer.Ordinal).ToArray();
		public bool TryRead(string path, long maxBytes, out byte[] data) => files.TryGetValue(MpqArchive.NormalizePath(path), out data!);
		public void Dispose() { }
	}
	private static void Check(bool value, string what) { if (!value) throw new Exception("Assertion failed " + what); }
	private static void U32(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);

	internal static byte[] ManyFrameDc6(int frames)
	{
		var one = LegacyFormatContracts.Dc6(); var frame = one.AsSpan(28, 41).ToArray();
		int table = 24 + frames * 4; var data = new byte[table + frames * frame.Length];
		one.AsSpan(0, 24).CopyTo(data); U32(data, 16, 1); U32(data, 20, (uint)frames);
		for (int i = 0; i < frames; i++) { int at = table + i * frame.Length; U32(data, 24 + i * 4, (uint)at); frame.CopyTo(data, at); }
		return data;
	}

	public static void Run(Action<string, Action> test)
	{
		test("DC6 accepts large glyph fonts (11,374 frames) up to the 16,384-frame cap", () =>
		{
			var font = Dc6Image.Parse(ManyFrameDc6(11374));
			Check(font.FramesPerDirection == 11374 && font.DecodeFrame(0, 11373).Width == 3, "large font");
			try { Dc6Image.Parse(ManyFrameDc6(Dc6Image.MaxFrames + 1)); throw new Exception("over cap accepted"); } catch (InvalidDataException) { }
		});

		test("COF with one frame accepts a 4-byte padded event block and still rejects other lengths", () =>
		{
			// 1 layer, 1 frame, 1 direction; component 1; events padded to 4 bytes; order = [1].
			var cof = new byte[28 + 9 + 4 + 1]; cof[0] = 1; cof[1] = 1; cof[2] = 1; cof[3] = 20;
			cof[28] = 1; "hth"u8.CopyTo(cof.AsSpan(33)); cof[^1] = 1;
			var parsed = CofAnimation.Parse(cof);
			Check(parsed.DrawOrder(0, 0).SequenceEqual(new byte[] { 1 }), "padded order");
			try { CofAnimation.Parse([.. cof, 0]); throw new Exception("trailing byte accepted"); } catch (InvalidDataException) { }
			var bad = (byte[])cof.Clone(); bad[^1] = 0;
			try { CofAnimation.Parse(bad); throw new Exception("non-permutation accepted"); } catch (InvalidDataException) { }
		});

		test("DS1 tolerates genuine shapes with Notes: raw act 5, absent trailing blocks, short group list, unparsed v13 paths", () =>
		{
			var act5 = MapContracts.Ds1(version: 18); U32(act5, 12, 5);
			Check(Ds1Map.Parse(act5).Act == 5, "act 5 clamps");
			var noPaths = MapContracts.Ds1(version: 18); var trimmed = noPaths[..^4]; // drop the empty path count
			var map = Ds1Map.Parse(trimmed); Check(map.Notes.Contains("path block absent") && map.Paths.Count == 0, "absent path block");
			var rich13 = MapContracts.Ds1(version: 13, rich: true);
			// Declared 2 groups while only 1 is stored, followed by an empty path count.
			int groupCount = rich13.Length - 20 - 4; U32(rich13, groupCount, 2);
			var shortGroups = Ds1Map.Parse([.. rich13, 0, 0, 0, 0]);
			Check(shortGroups.Groups.Count == 1 && shortGroups.Notes.Any(n => n.StartsWith("group list short")), "short groups");
			var oddPaths = Ds1Map.Parse([.. MapContracts.Ds1(version: 13, rich: true), 1, 0, 0, 0, 5, 0, 0, 0, 3, 0, 0, 0, 3, 0, 0, 0]);
			Check(oddPaths.Paths.Count == 0 && oddPaths.Notes.Any(n => n.StartsWith("pre-v14 path block not parsed")), "unparsed v13 paths");
			var act6 = MapContracts.Ds1(version: 18); U32(act6, 12, 6);
			try { Ds1Map.Parse(act6); throw new Exception("act 6 accepted"); } catch (InvalidDataException) { }
		});

		test("excel prose file is classified as documentation, not a table", () =>
		{
			Check(AssetDecoders.Kind("data\\global\\excel\\AiParms.txt") == "excel_doc" && AssetDecoders.Kind("data\\global\\excel\\levels.txt") == "excel_txt", "kinds");
			AssetDecoders.Validate("excel_doc", Encoding.Latin1.GetBytes("Description of Ai Parameters\r\nP1\tPct chance\r\n"));
			try { AssetDecoders.Validate("excel_doc", [65, 0, 66]); throw new Exception("NUL accepted"); } catch (InvalidDataException) { }
		});

		test("corpus validation reports per-kind results and separates failures in unreferenced DT1 files", () =>
		{
			var files = new Dictionary<string, byte[]>
			{
				["data\\global\\excel\\lvltypes.txt"] = Encoding.Latin1.GetBytes("Name\tFile 1\tFile 2\r\nAct 1 - Town\tAct1/Town/used.dt1\t0\r\n"),
				["data\\global\\tiles\\act1\\town\\used.dt1"] = [4, 0, 0, 0, 1, 0, 0, 0],
				["data\\global\\tiles\\act1\\old\\leftover.dt1"] = [4, 0, 0, 0, 1, 0, 0, 0],
				["data\\global\\ui\\a.dc6"] = LegacyFormatContracts.Dc6(),
			};
			var report = CorpusValidation.Run(new MemorySource(files));
			var dt1 = report.Kinds.Single(k => k.Kind == "dt1");
			Check(dt1.Failed == 1 && dt1.Unreferenced == 1 && dt1.Failures[0].Contains("used.dt1") && dt1.UnreferencedFailures[0].Contains("leftover.dt1"), "dt1 buckets");
			Check(report.Kinds.Single(k => k.Kind == "dc6").Passed == 1 && report.Kinds.Single(k => k.Kind == "excel_txt").Passed == 1, "other kinds");
			Check(report.Resources == 4 && report.Failed == 1 && report.Unreferenced == 1 && report.Skipped == 0 && report.Notes.Count == 0, "totals");
		});

		test("corpus validation counts unreadable resources and unexpected exceptions instead of passing or aborting", () =>
		{
			var files = new Dictionary<string, byte[]>
			{
				["data\\global\\excel\\lvltypes.txt"] = [65, 0, 66],
				["data\\global\\ui\\a.dc6"] = LegacyFormatContracts.Dc6(),
				["data\\global\\ui\\big.dc6"] = LegacyFormatContracts.Dc6(),
				["data\\global\\ui\\boom.dc6"] = LegacyFormatContracts.Dc6(),
			};
			var report = CorpusValidation.Run(new FaultySource(files));
			var dc6 = report.Kinds.Single(k => k.Kind == "dc6");
			Check(dc6.Passed == 1 && dc6.Skipped == 1 && dc6.SkippedResources[0].Contains("big.dc6") && dc6.Failed == 1 && dc6.Failures[0].Contains("NotSupportedException"), "per-resource accounting");
			Check(report.Notes.Single().Contains("lvltypes.txt") && report.Kinds.Single(k => k.Kind == "excel_txt").Failed == 1, "bad lvltypes is a note and a failure, not an abort");
			Check(report.Resources == 4 && report.Skipped == 1 && !report.Clean, "skipped resources fail the run");
			Check(dc6.SkippedResources[0].Contains("returned no data"), "skip wording matches behaviour");
			var noRefs = new Dictionary<string, byte[]>
			{
				["data\\global\\excel\\lvltypes.txt"] = Encoding.Latin1.GetBytes("Name\tFile 1\r\nAct 1 - Town\t0\r\n"),
				["data\\global\\tiles\\act1\\town\\bad.dt1"] = [4, 0, 0, 0, 1, 0, 0, 0],
			};
			var unref = CorpusValidation.Run(new MemorySource(noRefs));
			Check(unref.Failed == 1 && unref.Unreferenced == 0 && !unref.Clean && unref.Notes.Single().Contains("references no .dt1"), "no references means no DT1 is reclassified");
		});
	}
}
