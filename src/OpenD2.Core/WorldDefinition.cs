using System.Security.Cryptography;

namespace OpenD2.Core;

public readonly record struct WorldRegion(string Name, CollisionGrid Collision)
{
	public RegionId Id => Collision.Region;
}
public readonly record struct WorldPortal(EntityId Id, RegionId Region, GamePosition Position, RegionId Destination, GamePosition Arrival);
public readonly record struct WorldNpc(EntityId Id, RegionId Region, GamePosition Position, string Name);
public enum QuestStage { Available, Active, ReadyToTurnIn, Completed }
public readonly record struct QuestProgress(QuestStage Stage, int Defeated, int Required);

// Bounded, preloaded content for one local player and one kill/return quest.
// All arrays are owned; live state belongs to GameSimulation, never to this definition.
public sealed class WorldDefinition
{
	public const int MaxRegions = 8, MaxPortals = 32, MaxQuestTargets = 32;
	private readonly WorldRegion[] regions;
	private readonly WorldPortal[] portals;
	private readonly EntityId[] questTargets;
	public ReadOnlySpan<WorldRegion> Regions => regions;
	public ReadOnlySpan<WorldPortal> Portals => portals;
	public ReadOnlySpan<EntityId> QuestTargets => questTargets;
	public WorldNpc QuestGiver { get; }
	public string QuestTitle { get; }
	public string ContentHash { get; }
	public WorldDefinition(IEnumerable<WorldRegion> regions, IEnumerable<WorldPortal> portals, WorldNpc questGiver,
		IEnumerable<EntityId> questTargets, string questTitle)
	{
		ArgumentNullException.ThrowIfNull(regions); ArgumentNullException.ThrowIfNull(portals); ArgumentNullException.ThrowIfNull(questTargets);
		this.regions = regions.Take(MaxRegions + 1).ToArray();
		if (this.regions.Length is < 1 or > MaxRegions || this.regions.Any(r => r.Collision is null || !ValidName(r.Name))) throw new ArgumentException("Invalid world regions.");
		Array.Sort(this.regions, (a, b) => a.Id.Value.CompareTo(b.Id.Value));
		if (this.regions.Select(r => r.Id).Distinct().Count() != this.regions.Length || this.regions.Sum(r => (long)r.Collision.Width * r.Collision.Height) > CollisionGrid.MaxCells)
			throw new ArgumentException("Duplicate region or aggregate collision budget exceeded.");
		this.portals = portals.Take(MaxPortals + 1).OrderBy(p => p.Id.Value).ToArray();
		if (this.portals.Length > MaxPortals) throw new ArgumentException("Portal budget exceeded.");
		var ids = new HashSet<EntityId>();
		foreach (var p in this.portals)
			if (p.Id.Value == 0 || !ids.Add(p.Id) || p.Region == p.Destination || !GetRegion(p.Region).Collision.CanOccupy(p.Position) || !GetRegion(p.Destination).Collision.CanOccupy(p.Arrival))
				throw new ArgumentException("Invalid portal, source or arrival.");
		if (questGiver.Id.Value == 0 || !ids.Add(questGiver.Id) || !ValidName(questGiver.Name) || !ValidName(questTitle) || !GetRegion(questGiver.Region).Collision.CanOccupy(questGiver.Position))
			throw new ArgumentException("Invalid quest NPC or title.");
		this.questTargets = questTargets.Take(MaxQuestTargets + 1).OrderBy(id => id.Value).ToArray();
		if (this.questTargets.Length is < 1 or > MaxQuestTargets) throw new ArgumentException("Invalid quest target count.");
		foreach (var id in this.questTargets) if (id.Value == 0 || !ids.Add(id)) throw new ArgumentException("Invalid or duplicate quest target.");
		QuestGiver = questGiver; QuestTitle = questTitle;
		ContentHash = HashContent();
	}
	public WorldRegion GetRegion(RegionId id)
	{
		foreach (var region in regions) if (region.Id == id) return region;
		throw new ArgumentException("Unknown world region.", nameof(id));
	}
	public bool TryGetPortal(EntityId id, out WorldPortal portal)
	{
		foreach (var item in portals) if (item.Id == id) { portal = item; return true; }
		portal = default; return false;
	}
	internal bool TryGetInteraction(EntityId id, out RegionId region, out GamePosition position)
	{
		if (id == QuestGiver.Id) { region = QuestGiver.Region; position = QuestGiver.Position; return true; }
		if (TryGetPortal(id, out var portal)) { region = portal.Region; position = portal.Position; return true; }
		region = default; position = default; return false;
	}
	private static bool ValidName(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 80 && !value.Any(char.IsControl);
	private string HashContent()
	{
		using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
		writer.Write(1); // world definition format, separate from simulation rules
		writer.Write(regions.Length);
		foreach (var r in regions)
		{
			var g = r.Collision; writer.Write(r.Id.Value); writer.Write(r.Name);
			writer.Write(g.Origin.X); writer.Write(g.Origin.Y); writer.Write(g.Width); writer.Write(g.Height);
			foreach (var cell in g.Cells) writer.Write((byte)cell);
		}
		writer.Write(portals.Length);
		foreach (var p in portals)
		{
			writer.Write(p.Id.Value); writer.Write(p.Region.Value); writer.Write(p.Position.X); writer.Write(p.Position.Y);
			writer.Write(p.Destination.Value); writer.Write(p.Arrival.X); writer.Write(p.Arrival.Y);
		}
		writer.Write(QuestGiver.Id.Value); writer.Write(QuestGiver.Region.Value); writer.Write(QuestGiver.Position.X); writer.Write(QuestGiver.Position.Y);
		writer.Write(QuestGiver.Name); writer.Write(QuestTitle); writer.Write(questTargets.Length);
		foreach (var id in questTargets) writer.Write(id.Value);
		writer.Flush(); return Convert.ToHexStringLower(SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length)));
	}
}
