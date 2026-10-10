using System.Text.Json;
using OpenD2.Assets;

if (args.Length == 0 || args[0] is "--help" or "-h")
{
	Console.WriteLine("Usage: OpenD2.AssetAudit --check-scene|--check-play-ready <game-data-directory> <scene-request.json>\n       OpenD2.AssetAudit --check-map <game-data-directory> <map-request.json>\n       OpenD2.AssetAudit --inspect-save <legacy-v96.d2s> (read-only header/checksum; no import)\n       OpenD2.AssetAudit [--probe|--decode|--decode-demo] <game-data-directory> [known-paths.txt]\n       OpenD2.AssetAudit --items-txt <game-data-directory> (lod-1.10f reference data; no gameplay import)\n       OpenD2.AssetAudit --tables-txt|--tables-bin-110f <game-data-directory>\nJSON goes to stdout. Scan uses read-only MPQs; no resource extraction. --decode validates Palette/text TBL/DC6/DCC/COF/DT1/DS1 and TXT structure. BIN schemas require explicit --tables-bin-110f.\nExit 0: scan completed without reported errors; 3: missing archives, read, decode or reference failures; 1: fatal error; 2: usage.\nA successful scan does not establish version compatibility or complete coverage.");
	return args.Length == 0 ? 2 : 0;
}
try
{
	if (args[0] is "--check-scene" or "--check-play-ready")
	{
		if (args.Length != 3) return 2;
		var installation = GameInstall.Probe(args[1]);
		var scene = LegacyPlayScene.Load(args[1], LegacySceneRequest.Read(args[2]));
		var readiness = PlaySceneReadiness.Check(scene);
		Console.WriteLine(JsonSerializer.Serialize(new { Installation = installation, Readiness = readiness, scene.ContentId, VersionStatus = "unverified", GameplayValidated = false,
			InventoryLayout = scene.Inventory.Entries, scene.Inventory.ContentHash, scene.ArtworkSources, scene.NpcArtworkSources, scene.NpcFacing, scene.HudArtworkSources, scene.ItemArtworkSources, ItemDefinitionSources = scene.ItemDefinitions?.Tables.Sources, ItemDefinitions = scene.ItemDefinitions?.Bindings.Select(p => new { Definition = p.Key.ToString(), Item = p.Value }), ArtworkActors = scene.Artwork.Keys.Select(id => id.Value), Maps = scene.Terrain.Select(p => new { Region = p.Key.Value, p.Value.Check }) }, new JsonSerializerOptions { WriteIndented = true }));
		return installation.MissingArchives.Count == 0 && (args[0] != "--check-play-ready" || readiness.ReadyForSceneGuiCheck) ? 0 : 3;
	}
	if (args[0] == "--check-map")
	{
		if (args.Length != 3) return 2;
		var installation = GameInstall.Probe(args[1]);
		var asset = LegacyMapAsset.Load(args[1], LegacyMapRequest.Read(args[2]));
		Console.WriteLine(JsonSerializer.Serialize(new { Installation = installation, Map = asset.Check }, new JsonSerializerOptions { WriteIndented = true }));
		asset.RequirePlayableTerrain();
		return installation.MissingArchives.Count == 0 ? 0 : 3;
	}
	if (args[0] == "--inspect-save")
	{
		if (args.Length != 2) return 2;
		Console.WriteLine(JsonSerializer.Serialize(LegacySaveInspector.Read(args[1]), new JsonSerializerOptions { WriteIndented = true }));
		return 0;
	}
	if (args[0] == "--items-txt")
	{
		if (args.Length != 2) return 2;
		var installation = GameInstall.Probe(args[1]);
		var tables = ItemTables.Load(path => AssetDecoders.ReadFromInstall(args[1], path));
		Console.WriteLine(JsonSerializer.Serialize(new { Installation = installation, Profile = ItemTables.Profile, VersionStatus = "unverified", GameplayValidated = false,
			ResourceExistenceChecked = false, tables.Sources, Items = tables.Items.Values.OrderBy(i => i.Code, StringComparer.Ordinal) }, new JsonSerializerOptions { WriteIndented = true }));
		return installation.MissingArchives.Count == 0 ? 0 : 3;
	}
	if (args[0] is "--tables-txt" or "--tables-bin-110f")
	{
		if (args.Length != 2) return 2;
		var mode = args[0] == "--tables-txt" ? MapTableMode.Txt : MapTableMode.Bin110f;
		var installation = GameInstall.Probe(args[1]);
		var tables = MapTables.Load(path => AssetDecoders.ReadFromInstall(args[1], path), mode);
		var issues = tables.Validate();
		Console.WriteLine(JsonSerializer.Serialize(new { Installation = installation, Mode = mode.ToString(), VersionStatus = "unverified", Complete = false, ResourceExistenceChecked = false,
			tables.LevelCount, tables.TypeCount, tables.PresetCount, tables.Sources, Issues = issues, DecoderVersion = AssetDecoders.Version }, new JsonSerializerOptions { WriteIndented = true }));
		return installation.MissingArchives.Count > 0 || issues.Any(i => i.IsError) ? 3 : 0;
	}
	string profile = "lod-1.10f";
	if (args[0] == "--decode-demo") { profile = "demo-1.04"; args[0] = "--decode"; }
	bool decode = args[0] == "--decode";
	if (decode) args = args[1..];
	if (args.Length == 0) return 2;
	bool probeOnly = args[0] == "--probe";
	if (decode && probeOnly) return 2;
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
	var report = AssetInventory.Scan(args[0], new AuditOptions(Decode: decode, Profile: profile), known);
	Console.WriteLine(JsonSerializer.Serialize(report, json));
	return report.Installation.MissingArchives.Count > 0 || report.Archives.Any(a => a.ErrorCode != null) || report.Entries.Any(e => e.ErrorCode != null) ? 3 : 0;
}
catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or JsonException)
{
	Console.Error.WriteLine(error.Message);
	return 1;
}
