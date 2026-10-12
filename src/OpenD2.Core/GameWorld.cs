namespace OpenD2.Core;

public enum InteractionFailure { Interrupted, OutOfRange, Obstructed, BlockedArrival }

public sealed partial class GameSimulation
{
	private readonly int worldPlayerIndex = -1;
	public const int MaxWorldPlayers = 4;
	private readonly int[] worldPlayers;
	private readonly EntityId[] interactions;
	private readonly GameCommand[] transferQueue;
	public WorldDefinition? World { get; }
	public int WorldPlayerCount => worldPlayers.Length;
	// Legacy single-player view and checkpoint anchor: the lowest player ID.
	// Simulation decisions always use the acting entity's region.
	public EntityId WorldPlayer => worldPlayerIndex < 0 ? default : entities[worldPlayerIndex].Id;
	public RegionId ActiveRegion => World is null ? Collision?.Region ?? default : entities[worldPlayerIndex].Region;
	// One stage per world player, in player order. Shared-quest worlds keep every entry equal; personal-quest worlds advance them independently.
	private readonly QuestStage[] playerQuests;
	// Compatibility view: the lowest player ID's stage (the same player WorldPlayer names). Use GetQuest for a specific player.
	public QuestStage QuestState => playerQuests.Length == 0 ? QuestStage.Available : playerQuests[0];
	public QuestProgress Quest => World is null ? default : new(QuestState, DefeatedTargets(), World.QuestTargets.Length);
	public QuestStage GetQuest(EntityId player) => playerQuests[PlayerOrdinal(player)];
	public QuestProgress GetQuestProgress(EntityId player) => World is null ? default : new(GetQuest(player), DefeatedTargets(), World.QuestTargets.Length);
	private int PlayerOrdinal(EntityId player)
	{
		for (int k = 0; k < worldPlayers.Length; k++) if (entities[worldPlayers[k]].Id == player) return k;
		throw new ArgumentException("Unknown world player.", nameof(player));
	}
	private int DefeatedTargets()
	{
		int defeated = 0;
		foreach (var id in World!.QuestTargets) if (!entities[indices[id]].IsAlive) defeated++;
		return defeated;
	}
	private void SetSharedQuest(QuestStage stage) => Array.Fill(playerQuests, stage);
	public bool IsRegionActive(RegionId region)
	{
		if (World is null) return Collision is null || Collision.Region == region;
		foreach (int index in worldPlayers) if (entities[index].Region == region) return true;
		return false;
	}
	private bool IsActive(EntityState entity) => IsRegionActive(entity.Region);
	private CollisionGrid? GridFor(EntityState entity) => World is null ? Collision : World.GetRegion(entity.Region).Collision;
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
			if (residents > MaxCombatEntities - World.PlayerLimit) throw new ArgumentException("Each region must leave combat slots for all players.");
		}
	}
	private void UpdateQuest(long tick)
	{
		if (World is null) return;
		if (World.PersonalQuests)
		{
			// The kills are shared, so every player who had already accepted learns about it; each hears it themselves.
			if (DefeatedTargets() != World.QuestTargets.Length) return;
			for (int k = 0; k < worldPlayers.Length; k++)
			{
				if (playerQuests[k] != QuestStage.Active) continue;
				playerQuests[k] = QuestStage.ReadyToTurnIn;
				var member = entities[worldPlayers[k]];
				Emit(tick, SimulationEventKind.QuestChanged, member, member.Position, (int)QuestStage.ReadyToTurnIn, World.QuestGiver.Id);
			}
			return;
		}
		if (QuestState != QuestStage.Active) return;
		var quest = Quest;
		if (quest.Defeated != quest.Required) return;
		SetSharedQuest(QuestStage.ReadyToTurnIn);
		var player = entities[worldPlayerIndex];
		Emit(tick, SimulationEventKind.QuestChanged, player, player.Position, (int)QuestState, World.QuestGiver.Id);
	}
	private void Interact(long tick)
	{
		// Players are ordered by ID. One shared quest transition per tick prevents
		// two simultaneous dialogues from accepting and redeeming the same quest.
		bool questChanged = false;
		for (int i = 0; i < worldPlayers.Length; i++)
			if (interactions[i] != default) Interact(worldPlayers[i], interactions[i], tick, ref questChanged);
	}
	private void Interact(int index, EntityId interaction, long tick, ref bool questChanged)
	{
		var player = entities[index];
		World!.TryGetInteraction(interaction, out _, out var position);
		InteractionFailure? failure = !canAct[index] || attacked[index] || !player.IsAlive ? InteractionFailure.Interrupted :
			DistanceSquared(player.Position, position) > (long)AttackRange * AttackRange ? InteractionFailure.OutOfRange :
			!GridFor(player)!.HasMeleeLine(player.Position, position) ? InteractionFailure.Obstructed : null;
		if (failure is { } reason) { Emit(tick, SimulationEventKind.InteractionFailed, player, position, (int)reason, interaction); return; }
		if (interaction == World.QuestGiver.Id)
		{
			if (World.PersonalQuests) { TalkPersonal(index, player, position, tick, interaction); return; }
			var before = QuestState;
			if (!questChanged && QuestState == QuestStage.Available)
			{
				var quest = Quest;
				SetSharedQuest(quest.Defeated == quest.Required ? QuestStage.ReadyToTurnIn : QuestStage.Active);
			}
			else if (!questChanged && QuestState == QuestStage.ReadyToTurnIn)
			{
				SetSharedQuest(QuestStage.Completed);
				foreach (int member in worldPlayers)
					if (entities[member].IsAlive) entities[member] = entities[member] with { Health = entities[member].MaxHealth }; // one-time shared reward
			}
			Emit(tick, SimulationEventKind.NpcTalked, player, position, (int)QuestState, interaction);
			if (before != QuestState)
			{ questChanged = true; Emit(tick, SimulationEventKind.QuestChanged, player, player.Position, (int)QuestState, interaction); }
			return;
		}
		World.TryGetPortal(interaction, out var portal);
		var destination = World.GetRegion(portal.Destination).Collision;
		bool blocked = !destination.CanOccupy(portal.Arrival);
		foreach (var e in entities)
			if (e.IsAlive && e.Region == portal.Destination && Overlaps(portal.Arrival, e.Position)) blocked = true;
		if (blocked) { Emit(tick, SimulationEventKind.InteractionFailed, player, position, (int)InteractionFailure.BlockedArrival, interaction); return; }
		// Destination is ready and validated before any ownership/state mutation.
		// Only this player's old-region inputs are canceled; peers keep their queues.
		int canceled = CancelRegionCommands(player.Id);
		inputs[index] = inputs[index] with { Tick = tick };
		entities[index] = player with { Region = portal.Destination, Position = portal.Arrival, MoveX = 0, MoveY = 0 };
		if (index == worldPlayerIndex) Collision = destination;
		events[eventCount++] = new(tick, SimulationEventKind.RegionChanged, player.Id, player.Region, player.Position, portal.Arrival, canceled, portal.Id, portal.Destination);
	}
	// Personal quests: only the talking player's own stage changes, so several players can accept or hand in on the same tick.
	// Completing heals that player (always alive here: dead or interrupted players already failed the interaction checks).
	private void TalkPersonal(int index, EntityState player, GamePosition position, long tick, EntityId interaction)
	{
		int k = Array.IndexOf(worldPlayers, index); var before = playerQuests[k];
		if (before == QuestStage.Available) playerQuests[k] = DefeatedTargets() == World!.QuestTargets.Length ? QuestStage.ReadyToTurnIn : QuestStage.Active;
		else if (before == QuestStage.ReadyToTurnIn)
		{
			playerQuests[k] = QuestStage.Completed;
			entities[index] = entities[index] with { Health = entities[index].MaxHealth };
		}
		Emit(tick, SimulationEventKind.NpcTalked, player, position, (int)playerQuests[k], interaction);
		if (before != playerQuests[k]) Emit(tick, SimulationEventKind.QuestChanged, player, player.Position, (int)playerQuests[k], interaction);
	}
	private int CancelRegionCommands(EntityId actor)
	{
		if (worldPlayers.Length == 1)
		{ int total = commands.Count; commands.Clear(); counts.Clear(); return total; }
		int retained = 0, canceled = 0;
		while (commands.TryDequeue(out var command, out _))
		{
			if (command.Actor != actor) { transferQueue[retained++] = command; continue; }
			canceled++;
			int count = counts[command.Tick] - 1;
			if (count == 0) counts.Remove(command.Tick); else counts[command.Tick] = count;
		}
		for (int i = 0; i < retained; i++)
		{
			var command = transferQueue[i];
			commands.Enqueue(command, (command.Tick, command.Actor.Value, command.Sequence));
		}
		return canceled;
	}
}
