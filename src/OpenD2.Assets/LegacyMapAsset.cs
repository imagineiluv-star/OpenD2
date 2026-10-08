using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenD2.Assets;

// User-owned paths only. This profile is a requested format, never proof of the installation version.
public sealed record LegacyMapRequest(int SchemaVersion, string Profile, string MapPath, string PalettePath, string[] Tilesets)
{
	public static LegacyMapRequest Read(string file)
	{
		using var input = File.OpenRead(file);
		if (input.Length > 65536) throw new InvalidDataException("Map request exceeds 64 KiB.");
		var request = JsonSerializer.Deserialize<LegacyMapRequest>(input, new JsonSerializerOptions
		{ MaxDepth = 8, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow }) ?? throw new InvalidDataException("Map request is empty.");
		request.Validate(); return request;
	}
	public void Validate()
	{
		if (SchemaVersion != 1 || Profile != "lod-1.10f") throw new InvalidDataException("Expected map request schema 1 / lod-1.10f profile.");
		if (AssetDecoders.Kind(MapPath) != "ds1" || AssetDecoders.Kind(PalettePath) != "palette") throw new InvalidDataException("Expected DS1 and Act palette paths.");
		if (Tilesets is null || Tilesets.Length is < 1 or > 32 || Tilesets.Any(p => AssetDecoders.Kind(p) != "dt1")) throw new InvalidDataException("Expected 1..32 DT1 paths.");
		if (Tilesets.Select(MpqArchive.NormalizePath).Distinct().Count() != Tilesets.Length) throw new InvalidDataException("Duplicate DT1 paths are not allowed.");
	}
}

public sealed record LegacyAssetSource(string Path, int Bytes, string Sha256);
public sealed record LegacyMapCheck(string Profile, string VersionStatus, bool Complete, bool GameplayValidated,
	int Width, int Height, int MissingTiles, int DuplicateKeys, int WalkableCells, IReadOnlyList<LegacyAssetSource> Sources);

// Shared by command-line preflight and the client. Does not extract files or mutate the installation.
public sealed class LegacyMapAsset
{
	public const long MaxInputBytes = 64L * 1024 * 1024;
	public MapScene Scene { get; }
	public Palette Palette { get; }
	public LegacyMapCheck Check { get; }
	private LegacyMapAsset(MapScene scene, Palette palette, LegacyMapCheck check) { Scene = scene; Palette = palette; Check = check; }
	public static LegacyMapAsset Load(LegacyMapRequest request, Func<string, byte[]> read, TileFrameCache? cache = null)
	{
		ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(read); request.Validate();
		// Snapshot caller-owned path arrays before invoking external readers.
		string mapPath = MpqArchive.NormalizePath(request.MapPath), palettePath = MpqArchive.NormalizePath(request.PalettePath);
		var names = request.Tilesets.Select(MpqArchive.NormalizePath).ToArray();
		var sources = new List<LegacyAssetSource>(); long inputBytes = 0;
		byte[] Read(string path)
		{
			var bytes = read(path); inputBytes += bytes.LongLength;
			if (bytes.Length > AssetDecoders.MaxInputBytes || inputBytes > MaxInputBytes) throw new InvalidDataException("Map input budget exceeded.");
			sources.Add(new(path, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)))); return bytes;
		}
		var palette = Palette.Parse(Read(palettePath));
		var map = Ds1Map.Parse(Read(mapPath)); var sets = new List<MapTileset>();
		foreach (string name in names) sets.Add(new(name, Dt1Tileset.Parse(Read(name))));
		var scene = MapScene.Build(map, sets, cache); int open = 0;
		for (int y = 0; y < map.Height * 5; y++) for (int x = 0; x < map.Width * 5; x++) if (!scene.CollisionAt(x, y).BlocksWalk) open++;
		return new(scene, palette, new(request.Profile, "unverified", false, false, map.Width, map.Height,
			scene.MissingTiles, scene.DuplicateKeys, open, sources.AsReadOnly()));
	}
	public static LegacyMapAsset Load(string directory, LegacyMapRequest request, TileFrameCache? cache = null) =>
		Load(request, path => AssetDecoders.ReadFromInstall(directory, path), cache);
	public void RequirePlayableTerrain()
	{
		if (Check.MissingTiles != 0 || Check.WalkableCells == 0) throw new InvalidDataException("Playable terrain needs every referenced tile and at least one known walkable cell.");
		// The simulation applies a stricter aggregate navigation-cell budget than the inspection viewer.
		if ((long)Check.Width * Check.Height * 25 > OpenD2.Core.CollisionGrid.MaxCells) throw new InvalidDataException("Playable terrain exceeds navigation budget.");
	}
}
