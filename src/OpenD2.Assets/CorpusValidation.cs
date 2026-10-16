using System.Text;

namespace OpenD2.Assets;

/// <summary>Unreferenced counts failures in files no level table points at (shipped leftovers, e.g. old DT1 4.1 tilesets).</summary>
public sealed record KindValidation(string Kind, int Passed, int Failed, int Unreferenced, IReadOnlyList<string> Failures, IReadOnlyList<string> UnreferencedFailures);
public sealed record CorpusValidationReport(string Directory, int Resources, int Passed, int Failed, int Unreferenced, IReadOnlyList<KindValidation> Kinds);

// Decodes every resource the decoders recognise in a user-supplied installation. Reports only
// logical paths and error messages; no resource content leaves the user's machine.
public static class CorpusValidation
{
	public static CorpusValidationReport Run(string directory, int failuresPerKind = 200)
	{
		using var source = new MpqAssetSource(directory);
		return Run(source, failuresPerKind, directory);
	}

	public static CorpusValidationReport Run(IAssetSource source, int failuresPerKind = 200, string directory = "", Func<string, bool>? kinds = null)
	{
		var referencedTiles = ReferencedTilesets(source);
		var buckets = new SortedDictionary<string, Bucket>(StringComparer.Ordinal);
		foreach (string name in source.List())
		{
			string? kind = AssetDecoders.Kind(name);
			if (kind is null || kinds?.Invoke(kind) == false) continue;
			if (!buckets.TryGetValue(kind, out var bucket)) buckets[kind] = bucket = new();
			try
			{
				if (!source.TryRead(name, AssetDecoders.MaxInputBytes, out var data)) continue;
				AssetDecoders.Validate(kind, data);
				bucket.Passed++;
			}
			catch (Exception error) when (error is InvalidDataException or ArgumentException or IOException or IndexOutOfRangeException or OverflowException)
			{
				string line = $"{name}: {error.GetType().Name}: {error.Message}";
				if (kind == "dt1" && referencedTiles is not null && !referencedTiles.Contains(name))
				{ bucket.Unreferenced++; if (bucket.UnreferencedFailures.Count < failuresPerKind) bucket.UnreferencedFailures.Add(line); }
				else { bucket.Failed++; if (bucket.Failures.Count < failuresPerKind) bucket.Failures.Add(line); }
			}
		}
		var list = buckets.Select(k => new KindValidation(k.Key, k.Value.Passed, k.Value.Failed, k.Value.Unreferenced, k.Value.Failures, k.Value.UnreferencedFailures)).ToArray();
		return new(directory, list.Sum(k => k.Passed + k.Failed + k.Unreferenced), list.Sum(k => k.Passed), list.Sum(k => k.Failed), list.Sum(k => k.Unreferenced), list);
	}

	private sealed class Bucket { public int Passed, Failed, Unreferenced; public List<string> Failures = [], UnreferencedFailures = []; }

	// Every .dt1 named in LvlTypes.txt, as logical paths. Null when the table is absent (nothing is reclassified).
	internal static HashSet<string>? ReferencedTilesets(IAssetSource source)
	{
		if (!source.TryRead("data\\global\\excel\\lvltypes.txt", AssetDecoders.MaxInputBytes, out var data)) return null;
		var table = ExcelTextTable.Parse(data);
		var set = new HashSet<string>(StringComparer.Ordinal);
		foreach (var row in table.Rows)
			foreach (var cell in row)
				if (cell.EndsWith(".dt1", StringComparison.OrdinalIgnoreCase))
					set.Add(MpqArchive.NormalizePath("data\\global\\tiles\\" + cell));
		return set;
	}
}
