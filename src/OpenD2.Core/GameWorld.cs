namespace OpenD2.Core;

public enum InteractionFailure { Interrupted, OutOfRange, Obstructed, BlockedArrival }

public sealed partial class GameSimulation
{
	private readonly int worldPlayerIndex = -1;
	private EntityId interaction;
	public WorldDefinition? World { get; }
	public EntityId WorldPlayer => worldPlayerIndex < 0 ? default : entities[worldPlayerIndex].Id;
	public RegionId ActiveRegion => World is null ? Collision?.Region ?? default : entities[worldPlayerIndex].Region;
	public QuestStage QuestState { get; private set; }
	public QuestProgress Quest
	{
		get
		{
			if (World is null) return default;
			int defeated = 0;
			foreach (var id in World.QuestTargets) if (!entities[indices[id]].IsAlive) defeated++;
			return new(QuestState, defeated, World.QuestTargets.Length);
		}
	}
	private bool IsActive(EntityState entity) => World is null || entity.Region == ActiveRegion;
	private void ValidateWorld()
	{
		if (World is null) return;
		foreach (var portal in World.Portals) if (indices.ContainsKey(portal.Id)) throw new ArgumentException("Portal ID overlaps a combat entity.");
		if (indices.ContainsKey(World.QuestGiver.Id)) throw new ArgumentException("NPC ID overlaps a combat entity.");
		foreach (var target in World.QuestTargets)
			if (!indices.TryGetValue(target, out int index) || entities[index].Kind != EntityKind.Monster) throw new ArgumentException("Quest target must identify a monster.");
		foreach (var region in World.Regions)
		{
			int residents = 0;
			foreach (var entity in entities) if (entity.Region == region.Id && entity.Kind != EntityKind.Player) residents++;
			if (residents >= MaxCombatEntities) throw new ArgumentException("Each region must leave a combat slot for the player.");
		}
	}
	private void UpdateQuest(long tick)
	{
		if (World is null || QuestState != QuestStage.Active) return;
		var quest = Quest;
		if (quest.Defeated != quest.Required) return;
		QuestState = QuestStage.ReadyToTurnIn;
		var player = entities[worldPlayerIndex];
		Emit(tick, SimulationEventKind.QuestChanged, player, player.Position, (int)QuestState, World.QuestGiver.Id);
	}
	private void Interact(long tick)
	{
		if (World is null || interaction == default) return;
		var player = entities[worldPlayerIndex];
		World.TryGetInteraction(interaction, out _, out var position);
		InteractionFailure? failure = !canAct[worldPlayerIndex] || attacked[worldPlayerIndex] || !player.IsAlive ? InteractionFailure.Interrupted :
			DistanceSquared(player.Position, position) > (long)AttackRange * AttackRange ? InteractionFailure.OutOfRange :
			!Collision!.HasMeleeLine(player.Position, position) ? InteractionFailure.Obstructed : null;
		if (failure is { } reason) { Emit(tick, SimulationEventKind.InteractionFailed, player, position, (int)reason, interaction); return; }
		if (interaction == World.QuestGiver.Id)
		{
			var before = QuestState;
			if (QuestState == QuestStage.Available)
			{
				var quest = Quest;
				QuestState = quest.Defeated == quest.Required ? QuestStage.ReadyToTurnIn : QuestStage.Active;
			}
			else if (QuestState == QuestStage.ReadyToTurnIn)
			{
				QuestState = QuestStage.Completed;
				entities[worldPlayerIndex] = player with { Health = player.MaxHealth }; // one-time quest reward
			}
			Emit(tick, SimulationEventKind.NpcTalked, player, position, (int)QuestState, interaction);
			if (before != QuestState) Emit(tick, SimulationEventKind.QuestChanged, player, player.Position, (int)QuestState, interaction);
			return;
		}
		World.TryGetPortal(interaction, out var portal);
		var destination = World.GetRegion(portal.Destination).Collision;
		bool blocked = !destination.CanOccupy(portal.Arrival);
		foreach (var e in entities)
			if (e.IsAlive && e.Region == portal.Destination && Overlaps(portal.Arrival, e.Position)) blocked = true;
		if (blocked) { Emit(tick, SimulationEventKind.InteractionFailed, player, position, (int)InteractionFailure.BlockedArrival, interaction); return; }
		// Destination is ready and validated before any ownership/state mutation.
		// The single player's old-region inputs must not execute after returning later.
		int canceled = commands.Count; commands.Clear(); counts.Clear();
		inputs[worldPlayerIndex] = inputs[worldPlayerIndex] with { Tick = tick };
		entities[worldPlayerIndex] = player with { Region = portal.Destination, Position = portal.Arrival, MoveX = 0, MoveY = 0 };
		Collision = destination;
		events[eventCount++] = new(tick, SimulationEventKind.RegionChanged, player.Id, player.Region, player.Position, portal.Arrival, canceled, portal.Id, portal.Destination);
	}
}
