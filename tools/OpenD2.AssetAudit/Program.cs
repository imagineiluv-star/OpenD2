using System.Text.Json;
using OpenD2.Assets;

if (args.Length == 0 || args[0] is "--help" or "-h")
{
	Console.WriteLine("Usage: OpenD2.AssetAudit --check-demo-music <pinned-demo-data-directory> <demo-scene.json>\n       OpenD2.AssetAudit --check-scene|--check-play-ready <game-data-directory> <scene-request.json>\n       OpenD2.AssetAudit --check-map <game-data-directory> <map-request.json>\n       OpenD2.AssetAudit --inspect-save <legacy-v96.d2s> (read-only header/checksum; no import)\n       OpenD2.AssetAudit [--probe|--decode|--decode-demo] <game-data-directory> [known-paths.txt]\n       OpenD2.AssetAudit --items-txt <game-data-directory> (lod-1.10f reference data; no gameplay import)\n       OpenD2.AssetAudit --character-txt <game-data-directory> (lod-1.10f charstats/experience/skills; no gameplay import)\n       OpenD2.AssetAudit --monsters-txt <game-data-directory> (lod-1.10f monstats: code, name key, level, experience; no gameplay import)\n       OpenD2.AssetAudit --tables-txt|--tables-bin-110f <game-data-directory>\n       OpenD2.AssetAudit --validate-all [game-data-directory] (decode every known resource kind from the MPQs; per-kind pass/fail summary; D2_DATA_PATH)\n       OpenD2.AssetAudit --verify-install [game-data-directory] (SHA-256 manifest check; directory defaults to D2_DATA_PATH)\n       OpenD2.AssetAudit --build-cache [game-data-directory] [--cache <dir>] [--png dc6,dcc] [--include <path-prefix>] [--version <id>] [--trust-hash-cache] [--max-png-files <n>] (extract/convert into a cache outside the repo; D2_ASSET_CACHE)\nJSON goes to stdout. Scan uses read-only MPQs; no resource extraction. --decode validates Palette/text TBL/DC6/DCC/COF/DT1/DS1 and TXT structure. BIN schemas require explicit --tables-bin-110f.\nExit 0: scan completed without reported errors; 3: missing archives, read, decode or reference failures; 1: fatal error; 2: usage.\nA successful scan does not establish version compatibility or complete coverage.");
	return args.Length == 0 ? 2 : 0;
}
try
{
	if (args[0] == "--validate-all")
	{
		string data = D2DataPaths.ResolveDataPath(args.Length > 1 ? args[1] : null) ?? throw new ArgumentException("Pass a game data directory or set D2_DATA_PATH.");
		var validation = CorpusValidation.Run(data);
		Console.WriteLine(JsonSerializer.Serialize(validation, new JsonSerializerOptions { WriteIndented = true }));
		return validation.Failed == 0 ? 0 : 3;
	}
	if (args[0] is "--verify-install" or "--build-cache")
	{
		string? Option(string name) { int i = Array.IndexOf(args, name); return i > 0 && i + 1 < args.Length ? args[i + 1] : null; }
		string? positional = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : null;
		string data = D2DataPaths.ResolveDataPath(positional) ?? throw new ArgumentException("Pass a game data directory or set D2_DATA_PATH.");
		string cache = D2DataPaths.ResolveCachePath(Option("--cache"));
		// Full SHA-256 of every archive by default (~1 GB/s). The size+mtime hash cache is an explicit, weaker opt-in.
		var verification = InstallManifest.Default.Verify(data, args.Contains("--trust-hash-cache") ? Path.Combine(cache, "source-hashes.json") : null, Option("--version"));
		if (args[0] == "--verify-install")
		{
			Console.WriteLine(JsonSerializer.Serialize(verification, new JsonSerializerOptions { WriteIndented = true }));
			return verification.Verified ? 0 : 3;
		}
		if (!verification.Verified) { Console.Error.WriteLine("Installation does not match any known MPQ manifest; refusing to build the cache."); return 3; }
		var png = (Option("--png") ?? "dc6").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(k => k != "none").ToHashSet();
		string? include = Option("--include") is { } prefix ? OpenD2.Assets.MpqArchive.NormalizePath(prefix) : null;
		var options = new AssetCacheOptions { PngKinds = png, MaxPngFiles = Option("--max-png-files") is { } maxText ? (int.TryParse(maxText, out int maxFiles) && maxFiles > 0 ? maxFiles : throw new ArgumentException("--max-png-files must be a positive integer.")) : 250_000, Include = include is null ? null : n => n.StartsWith(include, StringComparison.Ordinal) };
		string target = Path.Combine(cache, verification.Version!);
		using var source = new MpqAssetSource(data);
		var watch = System.Diagnostics.Stopwatch.StartNew();
		var result = AssetCache.Build(source, AssetCache.Fingerprint(verification.Archives) + (include is null ? "" : "|" + include), target, options);
		Console.WriteLine(JsonSerializer.Serialize(new { version = verification.Version, cache = target, result.Rebuilt, result.Resources, result.PngFiles, result.PngBytes, result.PngCapped, result.Tables, result.Palettes,
			errorCount = result.Errors.Count, errors = result.Errors.Take(20), seconds = Math.Round(watch.Elapsed.TotalSeconds, 1) }, new JsonSerializerOptions { WriteIndented = true }));
		// A capped cache is incomplete (and is rebuilt next time); report it as a failure.
		return result.PngCapped ? 3 : 0;
	}
	if (args[0] == "--check-demo-music")
	{
		if (args.Length != 3) return 2;
		Console.WriteLine(JsonSerializer.Serialize(MusicProbe.CheckDemo(args[1], args[2]), new JsonSerializerOptions { WriteIndented = true }));
		return 0;
	}
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
	if (args[0] == "--character-txt")
	{
		if (args.Length != 2) return 2;
		var installation = GameInstall.Probe(args[1]);
		var tables = CharacterTables.Load(path => AssetDecoders.ReadFromInstall(args[1], path));
		var classes = Enum.GetValues<OpenD2.Core.CharacterClass>().Select(c => new { Class = c.ToString(), Definition = tables.Progression.Get(c), MaxLevel = tables.Progression.MaxLevel(c),
			Experience = Enumerable.Range(1, tables.Progression.MaxLevel(c)).Select(l => tables.Progression.ExperienceFor(c, l)).ToArray(),
			Skills = tables.Skills.All.Where(s => s.Class == c).OrderBy(s => s.Id).ToArray() }).ToArray();
		Console.WriteLine(JsonSerializer.Serialize(new { Installation = installation, Profile = CharacterTables.Profile, VersionStatus = "unverified", GameplayValidated = false,
			tables.Progression.Source, tables.Sources, Classes = classes }, new JsonSerializerOptions { WriteIndented = true }));
		return installation.MissingArchives.Count == 0 ? 0 : 3;
	}
	if (args[0] == "--monsters-txt")
	{
		if (args.Length != 2) return 2;
		var installation = GameInstall.Probe(args[1]);
		var tables = MonsterTables.Load(path => AssetDecoders.ReadFromInstall(args[1], path));
		Console.WriteLine(JsonSerializer.Serialize(new { Installation = installation, Profile = MonsterTables.Profile, VersionStatus = "unverified", GameplayValidated = false,
			tables.Monsters.Source, tables.Sources, Monsters = tables.Monsters.All.OrderBy(m => m.Code, StringComparer.OrdinalIgnoreCase).ToArray() }, new JsonSerializerOptions { WriteIndented = true }));
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
