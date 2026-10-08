using System.Security.Cryptography;
using System.Text.Json;
using OpenD2.Assets;

internal static class PlayAssetContracts
{
	internal static readonly LegacyMapRequest Request = new(1, "lod-1.10f", "data/global/tiles/test.ds1", "data/global/palette/act1/pal.dat", ["data/global/tiles/test.dt1"]);
	internal static byte[] Read(string path) => path.EndsWith(".ds1") ? MapContracts.Ds1() : path.EndsWith(".dt1") ? MapContracts.Dt1() : new byte[768];
	private static void Check(bool value) { if (!value) throw new Exception("Play asset assertion failed."); }
	private static void Bad(Action action)
	{ try { action(); } catch (Exception e) when (e is ArgumentException or InvalidDataException or JsonException) { return; } throw new Exception("Expected invalid play asset."); }
	public static void Run(string root, Action<string, Action> test)
	{
		test("selected map preflight records exact sources without claiming real-data acceptance", () =>
		{
			var asset = LegacyMapAsset.Load(Request, Read); asset.RequirePlayableTerrain();
			Check(asset.Check is { Width: 2, Height: 1, WalkableCells: 50, MissingTiles: 0, VersionStatus: "unverified", Complete: false, GameplayValidated: false });
			Check(asset.Check.Sources.Count == 3);
			foreach (var source in asset.Check.Sources)
				Check(source.Bytes == Read(source.Path).Length && source.Sha256 == Convert.ToHexStringLower(SHA256.HashData(Read(source.Path))));
		});
		test("map request rejects unsupported profiles, duplicate paths and traversal before I/O", () =>
		{
			foreach (var request in new[] { Request with { SchemaVersion = 2 }, Request with { Profile = "d2r" }, Request with { MapPath = "../a.ds1" },
				Request with { PalettePath = "a.txt" }, Request with { Tilesets = [] }, Request with { Tilesets = ["A.dt1", "a.dt1"] }, Request with { Tilesets = null! } })
				Bad(() => LegacyMapAsset.Load(request, _ => throw new Exception("Invalid request reached I/O.")));
		});
		test("map request strict JSON and size validation", () =>
		{
			string file = Path.Combine(root, "map-request.json"); File.WriteAllText(file, JsonSerializer.Serialize(Request));
			Check(LegacyMapRequest.Read(file).Tilesets.SequenceEqual(Request.Tilesets));
			foreach (string bad in new[] { "null", "{}", "{", new string(' ', 65537), JsonSerializer.Serialize(Request)[..^1] + ",\"Typo\":true}" })
			{ File.WriteAllText(file, bad); Bad(() => LegacyMapRequest.Read(file)); }
		});
		test("missing tiles remain visible to inspection but cannot become playable terrain", () =>
		{
			var asset = LegacyMapAsset.Load(Request, p => p.EndsWith(".dt1") ? MapContracts.Dt1(new MapContracts.Tile(Main: 2)) : Read(p));
			Check(asset.Check.MissingTiles == 2 && asset.Check.WalkableCells == 0); Bad(asset.RequirePlayableTerrain);
		});
		test("map preflight preserves read failures instead of replacing source content", () =>
		{
			try { LegacyMapAsset.Load(Request, _ => throw new FileNotFoundException("source absent")); throw new Exception("Expected missing resource."); }
			catch (FileNotFoundException) { }
			Bad(() => LegacyMapAsset.Load(Request, _ => new byte[AssetDecoders.MaxInputBytes + 1]));
		});
		test("map preflight snapshots paths and enforces total input budget", () =>
		{
			var request = Request with { Tilesets = ["a.dt1"] };
			var asset = LegacyMapAsset.Load(request, p => { request.Tilesets[0] = "../changed.dt1"; return Read(p); });
			Check(asset.Check.Sources[^1].Path == "a.dt1");
			var large = MapContracts.Dt1(); Array.Resize(ref large, AssetDecoders.MaxInputBytes);
			Bad(() => LegacyMapAsset.Load(Request with { Tilesets = ["a.dt1", "b.dt1", "c.dt1"] }, p => p.EndsWith(".dt1") ? large : Read(p)));
		});
	}
}
