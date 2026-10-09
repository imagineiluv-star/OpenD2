using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenD2.Core;

namespace OpenD2.Assets;

public sealed record PlayRegion(uint Id, string Name, LegacyMapRequest Terrain);
public sealed record PlaySpawn(uint Id, uint Region, int X, int Y, bool Player, int Health = 100);
public sealed record PlayNpc(uint Id, uint Region, int X, int Y, string Name);
public sealed record PlayPortal(uint Id, uint Region, int X, int Y, uint Destination, int ArrivalX, int ArrivalY);
public sealed record LegacySceneRequest(int SchemaVersion, string Title, PlayRegion[] Regions, PlaySpawn[] Actors,
	PlayNpc Npc, PlayPortal[] Portals, uint[] QuestTargets, LegacyActorRequest[]? Artwork = null, bool NavigateWalls = false, LegacyAudioRequest? Audio = null, LegacyNpcRequest? NpcArtwork = null, LegacyHudRequest? HudArtwork = null, LegacyItemRequest? ItemArtwork = null, LegacyItemDefinitionsRequest? ItemDefinitions = null)
{
	public static LegacySceneRequest Read(string file)
	{
		using var input = File.OpenRead(file);
		if (input.Length > 262144) throw new InvalidDataException("Scene request exceeds 256 KiB.");
		return JsonSerializer.Deserialize<LegacySceneRequest>(input, new JsonSerializerOptions
		{ MaxDepth = 12, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow }) ?? throw new InvalidDataException("Scene request is empty.");
	}
}

// Explicit placements avoid guessing DS1 object IDs, NPC meanings or original campaign rules.
public sealed class LegacyPlayScene
{
	public WorldDefinition World { get; }
	public IReadOnlyList<EntityState> Actors { get; }
	public IReadOnlyDictionary<RegionId, LegacyMapAsset> Terrain { get; }
	public string ContentId { get; }
	public IReadOnlyDictionary<EntityId, LegacyActorArt> Artwork { get; }
	public IReadOnlyList<LegacyAssetSource> ArtworkSources { get; }
	public LegacyAudioBank? Audio { get; }
	public LegacyActorArt? NpcArtwork { get; }
	public int NpcFacing { get; }
	public IReadOnlyList<LegacyAssetSource> NpcArtworkSources { get; }
	public LegacyHudArt? HudArtwork { get; }
	public IReadOnlyList<LegacyAssetSource> HudArtworkSources { get; }
	public LegacyItemArt? ItemArtwork { get; }
	public IReadOnlyList<LegacyAssetSource> ItemArtworkSources { get; }
	public LegacyItemDefinitions? ItemDefinitions { get; }
	private LegacyPlayScene(WorldDefinition world, EntityState[] actors, Dictionary<RegionId, LegacyMapAsset> terrain, Dictionary<EntityId, LegacyActorArt> artwork, LegacyAssetSource[] artworkSources, string id, LegacyAudioBank? audio, LegacyActorArt? npcArtwork, int npcFacing, LegacyAssetSource[] npcSources, LegacyHudArt? hudArtwork, LegacyAssetSource[] hudSources, LegacyItemArt? itemArtwork, LegacyAssetSource[] itemSources, LegacyItemDefinitions? itemDefinitions)
	{ World = world; Actors = Array.AsReadOnly(actors); Terrain = new ReadOnlyDictionary<RegionId, LegacyMapAsset>(terrain); Artwork = new ReadOnlyDictionary<EntityId, LegacyActorArt>(artwork); ArtworkSources = Array.AsReadOnly(artworkSources); ContentId = id; Audio = audio; NpcArtwork = npcArtwork; NpcFacing = npcFacing; NpcArtworkSources = Array.AsReadOnly(npcSources); HudArtwork = hudArtwork; HudArtworkSources = Array.AsReadOnly(hudSources); ItemArtwork = itemArtwork; ItemArtworkSources = Array.AsReadOnly(itemSources); ItemDefinitions = itemDefinitions; }
	public GameSimulation Create(uint seed) => new(seed, Actors, world: World);
	public static LegacyPlayScene Load(string directory, LegacySceneRequest request) => Load(request, path => AssetDecoders.ReadFromInstall(directory, path));
	public static LegacyPlayScene Load(LegacySceneRequest request, Func<string, byte[]> read)
	{
		ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(read);
		if (request.SchemaVersion != 1 || string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 80 || request.Title.Any(char.IsControl) ||
			request.Regions is null || request.Regions.Length is < 1 or > WorldDefinition.MaxRegions || request.Regions.Any(r => r is null || r.Terrain is null) ||
			request.Actors is null || request.Actors.Length is < 2 or > GameSimulation.MaxEntities || request.Actors.Any(a => a is null) ||
			request.Npc is null || request.Portals is null || request.Portals.Length > WorldDefinition.MaxPortals || request.Portals.Any(p => p is null) ||
			request.QuestTargets is null || request.QuestTargets.Length is < 1 or > WorldDefinition.MaxQuestTargets)
			throw new InvalidDataException("Invalid scene schema, title or content counts.");
		var definitionsRequest = request.ItemDefinitions is null ? null : LegacyItemDefinitions.Snapshot(request.ItemDefinitions);
		var itemRequest = request.ItemArtwork is null ? null : LegacyItemArt.Snapshot(request.ItemArtwork);
		var hudRequest = request.HudArtwork is null ? null : LegacyHudArt.Snapshot(request.HudArtwork);
		var npcRequest = request.NpcArtwork;
		if (npcRequest is not null)
		{
			if (npcRequest.Entity != request.Npc.Id || npcRequest.Idle is null) throw new InvalidDataException("NPC artwork must reference the scene quest giver and define Idle.");
			var idle = npcRequest.Idle;
			npcRequest = npcRequest with { Idle = idle with { Directions = idle.Directions?.ToArray()!, Layers = idle.Layers is null ? null : new(idle.Layers) } };
		}
		var regions = request.Regions.Select(r => r with { Terrain = r.Terrain with { Tilesets = r.Terrain.Tilesets?.ToArray()! } }).OrderBy(r => r.Id).ToArray();
		var artworkRequests = request.Artwork ?? [];
		if (artworkRequests.Length > 32 || artworkRequests.Any(a => a is null) || artworkRequests.Select(a => a.Entity).Distinct().Count() != artworkRequests.Length)
			throw new InvalidDataException("Expected at most 32 unique actor artwork profiles.");
		artworkRequests = artworkRequests.Select(a => a with { Motions = a.Motions?.Select(m => m is null ? null! : m with { Directions = m.Directions?.ToArray()!, Layers = m.Layers is null ? null : new(m.Layers) }).ToArray()! }).ToArray();
		var spawns = request.Actors.ToArray(); var portals = request.Portals.ToArray(); var targets = request.QuestTargets.ToArray(); var npc = request.Npc; string title = request.Title;
		if (regions.Any(r => r.Id == 0 || string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 80 || r.Name.Any(char.IsControl)) || regions.Select(r => r.Id).Distinct().Count() != regions.Length ||
			spawns.Count(a => a.Player) != 1 || spawns.Single(a => a.Player).Id != 1 || spawns.Any(a => a.Health is < 1 or > 100000))
			throw new InvalidDataException("Scene requires unique regions and exactly one player with ID 1 and valid health.");
		foreach (var region in regions) region.Terrain.Validate();
		var terrain = new Dictionary<RegionId, LegacyMapAsset>(); long input = 0, pixels = 0, cells = 0;
		byte[] Read(string path)
		{
			var bytes = read(path); input += bytes.LongLength;
			if (input > 128L * 1024 * 1024) throw new InvalidDataException("Scene input exceeds 128 MiB."); return bytes;
		}
		foreach (var region in regions)
		{
			var asset = LegacyMapAsset.Load(region.Terrain, Read); asset.RequirePlayableTerrain();
			cells += (long)asset.Check.Width * asset.Check.Height * 25;
			pixels += asset.Scene.Images.Sum(i => (long)i.Frame.Indices.Length);
			if (cells > CollisionGrid.MaxCells || pixels > Dt1Tileset.MaxPixels) throw new InvalidDataException("Scene collision or decoded pixel budget exceeded.");
			terrain.Add(new(region.Id), asset);
		}
		var world = new WorldDefinition(regions.Select(r => new WorldRegion(r.Name, terrain[new(r.Id)].Scene.ToCollisionGrid(new(r.Id)))),
			portals.Select(p => new WorldPortal(new(p.Id), new(p.Region), new(p.X, p.Y), new(p.Destination), new(p.ArrivalX, p.ArrivalY))),
			new(new(npc.Id), new(npc.Region), new(npc.X, npc.Y), npc.Name), targets.Select(id => new EntityId(id)), title, request.NavigateWalls);
		var actors = spawns.Select(a => new EntityState(new(a.Id), new(a.Region), new(a.X, a.Y), Kind: a.Player ? EntityKind.Player : EntityKind.Monster, Health: a.Health, MaxHealth: a.Health)).OrderBy(a => a.Id.Value).ToArray();
		_ = new GameSimulation(1, actors, world: world); // Validate IDs, ownership, spawns, quest targets and portal references before exposing content.
		var artwork = new Dictionary<EntityId, LegacyActorArt>(); var artworkSources = new List<LegacyAssetSource>();
		foreach (var art in artworkRequests.OrderBy(a => a.Entity))
		{
			if (!actors.Any(a => a.Id.Value == art.Entity)) throw new InvalidDataException("Artwork references an unknown combat actor.");
			var loaded = LegacyActorArt.Load(art, path =>
			{
				var bytes = Read(path); artworkSources.Add(new(path, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)))); return bytes;
			});
			pixels += loaded.PixelCount;
			if (pixels > Dt1Tileset.MaxPixels) throw new InvalidDataException("Combined terrain/artwork pixel budget exceeded.");
			artwork.Add(new(art.Entity), loaded);
		}
		LegacyActorArt? npcArtwork = null; var npcSources = new List<LegacyAssetSource>();
		if (npcRequest is not null)
		{
			npcArtwork = LegacyActorArt.LoadNpc(npcRequest, path =>
			{
				var bytes = Read(path); npcSources.Add(new(path, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)))); return bytes;
			});
			pixels += npcArtwork.PixelCount;
			if (pixels > Dt1Tileset.MaxPixels) throw new InvalidDataException("Combined terrain/actor/NPC pixel budget exceeded.");
		}
		LegacyHudArt? hudArtwork = null; var hudSources = new List<LegacyAssetSource>();
		if (hudRequest is not null)
		{
			hudArtwork = LegacyHudArt.Load(hudRequest, path =>
			{
				var bytes = Read(path); hudSources.Add(new(path, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)))); return bytes;
			});
			pixels += hudArtwork.PixelCount;
			if (pixels > Dt1Tileset.MaxPixels) throw new InvalidDataException("Combined scene/HUD pixel budget exceeded.");
		}
		LegacyItemArt? itemArtwork = null; var itemSources = new List<LegacyAssetSource>();
		if (itemRequest is not null)
		{
			itemArtwork = LegacyItemArt.Load(itemRequest, path =>
			{
				var bytes = Read(path); itemSources.Add(new(path, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)))); return bytes;
			});
			pixels += itemArtwork.PixelCount;
			if (pixels > Dt1Tileset.MaxPixels) throw new InvalidDataException("Combined scene/item pixel budget exceeded.");
		}
		var itemDefinitions = definitionsRequest is null ? null : LegacyItemDefinitions.Load(definitionsRequest, Read);
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		hash.AppendData(Encoding.UTF8.GetBytes(world.ContentHash)); hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(actors));
		foreach (var pair in terrain.OrderBy(p => p.Key.Value)) foreach (var source in pair.Value.Check.Sources)
		{ hash.AppendData(Encoding.UTF8.GetBytes(source.Path + "\n" + source.Sha256 + "\n")); }
		if (artworkRequests.Length > 0) hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(artworkRequests));
		foreach (var source in artworkSources) hash.AppendData(Encoding.UTF8.GetBytes(source.Path + "\n" + source.Sha256 + "\n"));
		if (npcRequest is not null)
		{
			hash.AppendData(Encoding.UTF8.GetBytes("npc-artwork-v1\n")); hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(npcRequest));
			foreach (var source in npcSources) hash.AppendData(Encoding.UTF8.GetBytes(source.Path + "\n" + source.Sha256 + "\n"));
		}
		if (hudRequest is not null)
		{
			hash.AppendData(Encoding.UTF8.GetBytes("hud-artwork-v1\n")); hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(hudRequest));
			foreach (var source in hudSources) hash.AppendData(Encoding.UTF8.GetBytes(source.Path + "\n" + source.Sha256 + "\n"));
		}
		if (itemRequest is not null)
		{
			hash.AppendData(Encoding.UTF8.GetBytes("item-artwork-v1\n")); hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(itemRequest));
			foreach (var source in itemSources) hash.AppendData(Encoding.UTF8.GetBytes(source.Path + "\n" + source.Sha256 + "\n"));
		}
		if (definitionsRequest is not null)
		{
			hash.AppendData(Encoding.UTF8.GetBytes("item-definitions-v1\n")); hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(definitionsRequest));
			foreach (var source in itemDefinitions!.Tables.Sources) hash.AppendData(Encoding.UTF8.GetBytes(source.Path + "\n" + source.Sha256 + "\n"));
		}
		var audio = request.Audio is null ? null : LegacyAudioBank.Load(request.Audio, regions.Select(r => new RegionId(r.Id)), Read);
		return new(world, actors, terrain, artwork, artworkSources.ToArray(), Convert.ToHexStringLower(hash.GetHashAndReset()), audio, npcArtwork, npcRequest?.Facing ?? 0, npcSources.ToArray(), hudArtwork, hudSources.ToArray(), itemArtwork, itemSources.ToArray(), itemDefinitions);
	}
}

public static class LegacyProjection
{
	// One DS1 tile is 5 navigation cells, 160 x 80 pixels. World coordinates are 256 units per cell.
	public static (double X, double Y) Project(double x, double y) => ((x - y) / 16, (x + y) / 32);
	public static GamePosition Unproject(double x, double y)
	{
		double wx = x * 8 + y * 16, wy = y * 16 - x * 8;
		if (!double.IsFinite(wx) || !double.IsFinite(wy) || Math.Abs(wx) > GameSimulation.PositionLimit || Math.Abs(wy) > GameSimulation.PositionLimit)
			throw new ArgumentOutOfRangeException(nameof(x));
		return new((int)Math.Round(wx), (int)Math.Round(wy));
	}
}
