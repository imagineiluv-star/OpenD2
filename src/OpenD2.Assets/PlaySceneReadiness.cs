using OpenD2.Core;

namespace OpenD2.Assets;

public sealed record PlaySceneReadiness(string ContentId, int Regions, int Actors, int ActorsWithArtwork,
	bool QuestLoopReachable, bool ReadyForSceneGuiCheck, bool OriginalRulesValidated, string GuiQa, IReadOnlyList<string> Issues, bool NpcArtworkConfigured = false, bool HudArtworkConfigured = false, int ItemArtworkCount = 0, int ItemDefinitionCount = 0)
{
	public bool ReadyForAllSpritesGuiCheck => ReadyForSceneGuiCheck && NpcArtworkConfigured;
	public static PlaySceneReadiness Check(LegacyPlayScene scene)
	{
		ArgumentNullException.ThrowIfNull(scene);
		var issues = new List<string>(); var world = scene.World;
		bool loop = CheckQuestLoop(world, scene.Actors, issues);
		foreach (var actor in scene.Actors) if (!scene.Artwork.ContainsKey(actor.Id)) issues.Add($"actor_artwork_missing:{actor.Id.Value}");
		return new(scene.ContentId, world.Regions.Length, scene.Actors.Count, scene.Artwork.Count, loop,
			loop && scene.Artwork.Count == scene.Actors.Count, false, "NOT_RUN", issues.AsReadOnly(), scene.NpcArtwork is not null, scene.HudArtwork is not null, scene.ItemArtwork?.Icons.Count ?? 0, scene.ItemDefinitions?.Bindings.Count ?? 0);
	}
	// Static terrain/portal connectivity only. Dynamic bodies, combat and actual GUI actions still need testing.
	public static bool CheckQuestLoop(WorldDefinition world, IReadOnlyList<EntityState> actors, ICollection<string> issues)
	{
		var labels = new Dictionary<RegionId, int[]>();
		foreach (var region in world.Regions) labels.Add(region.Id, Components(region.Collision));
		(RegionId Region, int Component) Node(RegionId region, GamePosition position)
		{
			var grid = world.GetRegion(region).Collision;
			if (!grid.CanOccupy(position)) throw new InvalidDataException("Readiness location is not walkable.");
			int x = (position.X - grid.Origin.X) / 256, y = (position.Y - grid.Origin.Y) / 256;
			return (region, labels[region][y * grid.Width + x]);
		}
		var edges = new Dictionary<(RegionId, int), List<(RegionId, int)>>();
		foreach (var portal in world.Portals)
		{
			var from = Node(portal.Region, portal.Position); var to = Node(portal.Destination, portal.Arrival);
			if (!edges.TryGetValue(from, out var next)) edges.Add(from, next = []); next.Add(to);
		}
		HashSet<(RegionId, int)> Reach((RegionId, int) start)
		{
			var visited = new HashSet<(RegionId, int)> { start }; var pending = new Queue<(RegionId, int)>(); pending.Enqueue(start);
			while (pending.TryDequeue(out var current)) if (edges.TryGetValue(current, out var next))
				foreach (var destination in next) if (visited.Add(destination)) pending.Enqueue(destination);
			return visited;
		}
		var player = actors.Single(a => a.Kind == EntityKind.Player);
		var npc = Node(world.QuestGiver.Region, world.QuestGiver.Position); bool ready = true;
		if (!Reach(Node(player.Region, player.Position)).Contains(npc)) { issues.Add("quest_giver_unreachable"); ready = false; }
		var outward = Reach(npc);
		foreach (var target in world.QuestTargets)
		{
			var actor = actors.Single(a => a.Id == target); var destination = Node(actor.Region, actor.Position);
			if (!outward.Contains(destination)) { issues.Add($"quest_target_unreachable:{target.Value}"); ready = false; }
			if (!Reach(destination).Contains(npc)) { issues.Add($"quest_return_unreachable:{target.Value}"); ready = false; }
		}
		return ready;
	}
	private static int[] Components(CollisionGrid grid)
	{
		int count = grid.Width * grid.Height, component = 0;
		var labels = new int[count]; var pending = new int[count];
		for (int start = 0; start < count; start++)
		{
			if (labels[start] != 0 || grid.Cells[start] != CollisionCell.Open) continue;
			component++; int head = 0, tail = 0; labels[start] = component; pending[tail++] = start;
			while (head < tail)
			{
				int at = pending[head++], x = at % grid.Width, y = at / grid.Width;
				void Visit(int nx, int ny)
				{
					if (grid.At(nx, ny) != CollisionCell.Open) return;
					int next = ny * grid.Width + nx; if (labels[next] != 0) return;
					labels[next] = component; pending[tail++] = next;
				}
				Visit(x - 1, y); Visit(x + 1, y); Visit(x, y - 1); Visit(x, y + 1);
			}
		}
		return labels;
	}
}
