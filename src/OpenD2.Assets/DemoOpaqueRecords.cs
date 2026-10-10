using System.Security.Cryptography;

namespace OpenD2.Assets;

// These two pinned demo entries have a .wav suffix but contain no RIFF/WAVE data.
// Their semantics are unknown: only exact byte identity is validated, never audio/playability.
// Apply solely to the explicitly selected demo profile and the original source archive.
public static class DemoOpaqueRecords
{
	private static readonly IReadOnlyDictionary<string, string> Hashes = new Dictionary<string, string>
	{
		[@"data\global\sfx\cursor\curindx.wav"] = "4d396a34d6ff49212ce414dfea0a1234484c5a850c70935a554c607d47ba7724",
		[@"data\global\sfx\cursor\wavindx.wav"] = "ad95a7e050cfb85068b04bc5bc4d5f898ec5c0a621e15a2506d3b3e60210e187"
	};
	public static bool IsRecord(string archive, string path) =>
		archive.Equals("d2sfx.mpq", StringComparison.OrdinalIgnoreCase) && Hashes.ContainsKey(MpqArchive.NormalizePath(path));
	public static void Validate(string path, ReadOnlySpan<byte> data)
	{
		AssetBinary.Require(Hashes.TryGetValue(MpqArchive.NormalizePath(path), out var hash) && data.Length == 72
			&& Convert.ToHexStringLower(SHA256.HashData(data)) == hash, "Unknown or modified demo opaque record.");
	}
}
