namespace OpenD2.Core;

public sealed partial class GameSimulation
{
	// Restore into a new owner; malformed state can never partially replace a live game.
	public static GameSimulation Restore(SimulationSnapshot state) => RestoreValidated(state, false);
	internal static GameSimulation RestoreLegacy(SimulationSnapshot state) => RestoreValidated(state, true);
	private static GameSimulation RestoreValidated(SimulationSnapshot state, bool legacy)
	{
		ArgumentNullException.ThrowIfNull(state);
		try { return RestoreChecked(state, legacy); }
		catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException)
		{ throw new InvalidDataException("Invalid simulation checkpoint.", error); }
	}
	private static GameSimulation RestoreChecked(SimulationSnapshot state, bool legacy = false)
	{
		if (state.RulesVersion != (legacy ? 4 : RulesVersion) || (!legacy && state.Inventory is null) || state.Tick < 0 || state.Tick > long.MaxValue - CommandHorizon || state.RandomState == 0 ||
			state.Entities is null || state.Entities.Count is < 1 or > MaxEntities || state.Inputs is null || state.Inputs.Count != state.Entities.Count ||
			state.PendingCommands is null || state.PendingCommands.Count > MaxPendingCommands || state.Items is null || state.Items.Count > state.Entities.Count || !Enum.IsDefined(state.QuestState))
			throw new InvalidDataException("Invalid checkpoint header or budgets.");
		var original = state.Entities.ToArray();
		var initial = original.Select(e => e with { Mode = MonsterMode.Idle, Target = default }).ToArray();
		var game = new GameSimulation(state.RandomState, initial, state.World is null ? state.Collision : null, state.World, legacy ? InventoryLayout.Legacy : state.Inventory);
		if (state.WorldPlayer != game.WorldPlayer || (state.World is null && state.QuestState != QuestStage.Available)) throw new InvalidDataException("Invalid world owner or quest.");
		for (int i = 0; i < original.Length; i++)
		{
			var e = original[i];
			if (e.Id != game.entities[i].Id || !Enum.IsDefined(e.Mode) ||
				(!e.IsAlive && (e.Mode != MonsterMode.Dead || e.MoveX != 0 || e.MoveY != 0 || e.HitStun != 0 || e.Target != default)) ||
				(e.IsAlive && e.Mode == MonsterMode.Dead) ||
				(e.Kind == EntityKind.Player && (e.Target != default || (e.IsAlive && e.Mode != MonsterMode.Idle) || e.AttackCooldown > PlayerAttackInterval)) ||
				(e.Target != default && (!game.indices.TryGetValue(e.Target, out var target) || game.entities[target].Kind != EntityKind.Player)))
				throw new InvalidDataException("Invalid saved entity state or ordering.");
			game.entities[i] = e;
			var cursor = state.Inputs[i];
			if (cursor.Actor != e.Id || cursor.Tick < 0 || cursor.Tick > state.Tick + CommandHorizon ||
				(cursor.Sequence == 0 ? cursor.Tick != 0 : cursor.Tick == 0) || (e.Kind != EntityKind.Player && cursor.Sequence != 0))
				throw new InvalidDataException("Invalid input cursor.");
		}
		game.Tick = state.Tick; game.QuestState = state.QuestState;
		if (game.World is not null && ((game.QuestState is QuestStage.ReadyToTurnIn or QuestStage.Completed) != (game.Quest.Defeated == game.Quest.Required) && game.QuestState != QuestStage.Available))
			throw new InvalidDataException("Quest stage disagrees with defeated targets.");
		var ids = new HashSet<ItemId>(); var slots = new HashSet<(EntityId, ItemLocation, int)>();
		foreach (var item in state.Items)
		{
			if (!ids.Add(item.Id) || !Enum.IsDefined(item.Definition) || !Enum.IsDefined(item.Location) ||
				!game.indices.TryGetValue(new(item.Id.Value), out int source) || game.entities[source].Kind != EntityKind.Monster || game.entities[source].IsAlive ||
				item.Definition != ((item.Id.Value & 1) == 0 ? ItemDefinition.TrainingSword : ItemDefinition.TrainingVest))
				throw new InvalidDataException("Invalid or duplicate item identity.");
			if (item.Location == ItemLocation.Ground)
			{
				var grid = state.World is null ? state.Collision : state.World.GetRegion(item.Region).Collision;
				if (item.Owner != default || item.Slot != -1 || grid is null || item.Region != grid.Region || !grid.CanOccupy(item.Position)) throw new InvalidDataException("Invalid ground item.");
			}
			else if (!game.indices.TryGetValue(item.Owner, out int owner) || game.entities[owner].Kind != EntityKind.Player || item.Region != default || item.Position != default ||
				item.Slot < 0 || (item.Location == ItemLocation.Inventory ? item.Slot >= (legacy ? 8 : InventoryCapacity) : item.Slot != (int)ItemCatalog.Get(item.Definition).Slot) || !slots.Add((item.Owner, item.Location, item.Slot)))
				throw new InvalidDataException("Invalid item owner or occupied slot.");
			if (item.Location == ItemLocation.Inventory)
			{
				ulong mask = game.Inventory.Mask(item.Definition, item.Slot);
				if (mask == 0 || (mask & game.Occupied(item.Owner)) != 0) throw new InvalidDataException("Inventory rectangles overlap or exceed the grid.");
			}
			game.items[game.itemCount++] = item;
		}
		// Queued commands may belong to a player who died after submission. Validate their
		// original payload/scheduling with a temporary live flag, then preserve the dead state.
		foreach (var command in state.PendingCommands.OrderBy(c => c.Tick).ThenBy(c => c.Actor.Value).ThenBy(c => c.Sequence))
		{
			if ((legacy && command.Kind == CommandKind.MoveItem) || !game.indices.TryGetValue(command.Actor, out int actor)) throw new InvalidDataException("Unknown queued actor.");
			var entity = game.entities[actor]; var cursor = state.Inputs[actor];
			if (command.Sequence > cursor.Sequence || command.Tick > cursor.Tick) throw new InvalidDataException("Queued command exceeds its cursor.");
			game.entities[actor] = entity with { Health = Math.Max(1, entity.Health) };
			var accepted = game.Submit(command); game.entities[actor] = entity;
			if (accepted != CommandResult.Accepted) throw new InvalidDataException($"Invalid queued command: {accepted}.");
		}
		for (int i = 0; i < game.inputs.Length; i++) game.inputs[i] = state.Inputs[i];
		return game;
	}
	public static GameSimulation Replay(SimulationSnapshot checkpoint, IReadOnlyList<RecordedCommand> trace, long targetTick)
	{
		ArgumentNullException.ThrowIfNull(trace);
		if (targetTick < checkpoint.Tick || targetTick - checkpoint.Tick > MaxReplayTicks || trace.Count > MaxReplayCommands) throw new ArgumentOutOfRangeException(nameof(targetTick));
		var game = Restore(checkpoint);
		foreach (var entry in trace)
		{
			if (entry.SubmittedAfterTick < game.Tick || entry.SubmittedAfterTick > targetTick) throw new InvalidDataException("Invalid replay submission time.");
			while (game.Tick < entry.SubmittedAfterTick) game.Step();
			if (game.Submit(entry.Command) != CommandResult.Accepted) throw new InvalidDataException("Rejected checkpoint replay command.");
		}
		while (game.Tick < targetTick) game.Step();
		return game;
	}
}
