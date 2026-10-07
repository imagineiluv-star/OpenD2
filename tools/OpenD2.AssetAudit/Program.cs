using System.Text.Json;
using OpenD2.Assets;

if (args.Length == 0 || args[0] is "--help" or "-h")
{
	Console.WriteLine("Usage: OpenD2.AssetAudit [--probe] <game-data-directory> [known-paths.txt]\nJSON goes to stdout. Scan uses read-only MPQs; no resource extraction or decoding.\nExit 0: scan completed without reported errors; 3: missing archives or read failures; 1: fatal error; 2: usage.\nA successful scan does not establish version compatibility or complete coverage.");
	return args.Length == 0 ? 2 : 0;
}
try
{
	bool probeOnly = args[0] == "--probe";
	if ((probeOnly && args.Length != 2) || (!probeOnly && args.Length > 2)) return 2;
	var json = new JsonSerializerOptions { WriteIndented = true };
	if (probeOnly)
	{
		var probe = GameInstall.Probe(args[1]);
		Console.WriteLine(JsonSerializer.Serialize(probe, json));
		return probe.MissingArchives.Count == 0 ? 0 : 3;
	}
	MpqArchive.VerifyBackend();
	IEnumerable<string>? known = null;
	if (args.Length == 2)
	{
		if (new FileInfo(args[1]).Length > 16 * 1024 * 1024) throw new InvalidDataException("Known-path file exceeds 16 MiB.");
		known = File.ReadLines(args[1]).Where(line => !string.IsNullOrWhiteSpace(line));
	}
	var report = AssetInventory.Scan(args[0], knownPaths: known);
	Console.WriteLine(JsonSerializer.Serialize(report, json));
	return report.Installation.MissingArchives.Count > 0 || report.Archives.Any(a => a.ErrorCode != null) || report.Entries.Any(e => e.ErrorCode != null) ? 3 : 0;
}
catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
{
	Console.Error.WriteLine(error.Message);
	return 1;
}
