namespace OpenD2.Npc;

public sealed record NpcModelSpec(string Id, string Name, string Repository, string Revision, string FileName, long Bytes, string Sha256)
{
	public Uri DownloadUri => new($"https://huggingface.co/{Repository}/resolve/{Revision}/{FileName}");
	public void Validate()
	{
		static bool Hex(string? s, int count) => s?.Length == count && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
		if (string.IsNullOrEmpty(Id) || Id.Length > 64 || !Id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-') ||
			string.IsNullOrWhiteSpace(Name) || Repository is not ("Qwen/Qwen3-0.6B-GGUF" or "Qwen/Qwen3-1.7B-GGUF") ||
			!Hex(Revision, 40) || !Hex(Sha256, 64) || Bytes is <= 0 or > 4L * 1024 * 1024 * 1024 ||
			string.IsNullOrEmpty(FileName) || !FileName.EndsWith(".gguf", StringComparison.Ordinal) || FileName.Length > 100 ||
			!FileName.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
			throw new ArgumentException("Invalid pinned NPC model specification.");
	}
}

public static class NpcModelCatalog
{
	public static NpcModelSpec Small { get; } = new("qwen3-06b-q4", "Qwen3 0.6B Q4_K_M (experimental)",
		"Qwen/Qwen3-0.6B-GGUF", "1208e45d782fe18602c5eaf10e5758d5b0f24c03", "Qwen3-0.6B-Q4_K_M.gguf", 396704416,
		"b0638f08417a2d3c8652760462eb5407c6e30173cf9608ad0820757a281eea0e");
	public static NpcModelSpec Comparison { get; } = new("qwen3-17b-q8", "Qwen3 1.7B Q8_0 (comparison)",
		"Qwen/Qwen3-1.7B-GGUF", "90862c4b9d2787eaed51d12237eafdfe7c5f6077", "Qwen3-1.7B-Q8_0.gguf", 1834426016,
		"061b54daade076b5d3362dac252678d17da8c68f07560be70818cace6590cb1a");
	public static IReadOnlyList<NpcModelSpec> All { get; } = Array.AsReadOnly(new[] { Small, Comparison });
}
