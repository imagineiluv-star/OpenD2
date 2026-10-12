namespace OpenD2.Core;

public sealed partial class GameSimulation
{
	// Life and mana derived from a character's progression, applied before the first tick like carried items.
	// Only players that are listed change; everyone else keeps the default resources.
	public GameSimulation WithPlayerResources(IReadOnlyList<(EntityId Actor, int Life, int Mana)> resources)
	{
		ArgumentNullException.ThrowIfNull(resources);
		if (Tick != 0 || PendingCommands != 0 || resources.Count > 4 || resources.Select(r => r.Actor).Distinct().Count() != resources.Count)
			throw new InvalidDataException("Player resources require an unstarted game with at most four distinct players.");
		var snapshot = CaptureSnapshot(); var entities = snapshot.Entities.ToArray();
		foreach (var (actor, life, mana) in resources)
		{
			int at = Array.FindIndex(entities, e => e.Id == actor && e.Kind == EntityKind.Player);
			if (at < 0 || life is < 1 or > 100_000 || mana is < 0 or > 100_000) throw new InvalidDataException("Invalid player resources.");
			entities[at] = entities[at] with { Health = life, MaxHealth = life, Mana = mana, MaxMana = mana };
		}
		return Restore(snapshot with { Entities = entities });
	}
}
