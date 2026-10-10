namespace OpenD2.Assets;

public sealed record InstallProbe(string Directory, string Profile, string VersionStatus,
	IReadOnlyList<string> Archives, IReadOnlyList<string> MissingArchives, IReadOnlyList<string> UnclassifiedArchives);

public static class GameInstall
{
	// Highest priority first, following the original OpenD2 search-path registration.
	// Filename presence alone is NOT proof of patch/version compatibility.
	public static IReadOnlyList<string> Priority { get; } = Array.AsReadOnly(new[]
	{
		"patch_d2.mpq", "d2xmusic.mpq", "d2xtalk.mpq", "d2xvideo.mpq", "d2exp.mpq",
		"d2kfixup.mpq", "d2delta.mpq", "d2video.mpq", "d2music.mpq", "d2speech.mpq", "d2sfx.mpq", "d2char.mpq", "d2data.mpq"
	});
	private static readonly string[] DemoArchives = ["patch_d2.mpq", "d2music.mpq", "d2speech.mpq", "d2sfx.mpq", "d2char.mpq", "d2data.mpq"];
	public static InstallProbe Probe(string directory, string profile = "lod-1.10f")
	{
		if (profile is not "lod-1.10f" and not "demo-1.04") throw new ArgumentException("Unknown installation profile.", nameof(profile));
		directory = DataDirectory.Validate(directory);
		var files = Directory.EnumerateFiles(directory).Where(p => Path.GetExtension(p).Equals(".mpq", StringComparison.OrdinalIgnoreCase)).Take(257).ToArray();
		if (files.Length > 256) throw new InvalidDataException("More than 256 archives in installation directory.");
		var groups = files.GroupBy(p => Path.GetFileName(p).ToLowerInvariant()).ToArray();
		if (groups.Any(g => g.Count() > 1)) throw new InvalidDataException("Ambiguous archive filenames differing only by case.");
		var map = groups.ToDictionary(g => g.Key, g => g.Single(), StringComparer.OrdinalIgnoreCase);
		var required = profile == "demo-1.04" ? DemoArchives : Priority.Where(n => n is not "d2delta.mpq" and not "d2kfixup.mpq");
		var missing = required.Where(n => !map.ContainsKey(n)).ToArray();
		var extra = map.Keys.Except(Priority, StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
		var ordered = Priority.Where(map.ContainsKey).Select(n => map[n]).Concat(extra.Select(n => map[n])).ToArray();
		return new InstallProbe(directory, profile, "unverified", ordered, missing, extra);
	}
}
