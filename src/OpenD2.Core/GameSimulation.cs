using System.Security.Cryptography;

namespace OpenD2.Core;

public readonly record struct RegionId(uint Value);
public readonly record struct EntityId(uint Value);
public readonly record struct GamePosition(int X, int Y);
public enum EntityKind { Player, Monster }
public enum MonsterMode { Idle, Chasing, Attacking, Dead }
public readonly record struct EntityState(EntityId Id, RegionId Region, GamePosition Position, int MoveX = 0, int MoveY = 0,
	EntityKind Kind = EntityKind.Player, int Health = 100, int MaxHealth = 100, int AttackCooldown = 0, int HitStun = 0,
	MonsterMode Mode = MonsterMode.Idle, EntityId Target = default, int Mana = 60, int MaxMana = 60,
	int SkillCooldown = 0, int ManaRecoveryTicks = 0, SkillId SelectedSkill = SkillId.PowerStrike)
{
	public bool IsAlive => Health > 0;
}
public enum CommandKind { SetMove, Signal, Attack, Interact, Pickup, Equip, Unequip, DropItem, MoveItem, SelectSkill, CastSkill }
public readonly record struct GameCommand(long Tick, ulong Sequence, EntityId Actor, RegionId Region, CommandKind Kind, int X = 0, int Y = 0, EntityId Target = default, ItemId Item = default);
public enum CommandResult { Accepted, InvalidCommand, UnknownActor, WrongRegion, ExpiredTick, TooFarAhead, StaleSequence, OutOfOrderTick, TickFull, QueueFull, DeadActor, NotPlayerControlled, InvalidTarget }
public enum SimulationEventKind { Moved, Signaled, Blocked, AttackStarted, Hit, Died, AttackFailed, MonsterChanged, RegionChanged, NpcTalked, QuestChanged, InteractionFailed, ItemDropped, ItemChanged, ItemFailed, SkillCast, SkillFailed }
public enum AttackFailure { Cooldown, OutOfRange, Obstructed, DeadTarget, Interrupted }
public readonly record struct SimulationEvent(long Tick, SimulationEventKind Kind, EntityId Actor, RegionId Region, GamePosition From, GamePosition To, int Value = 0, EntityId Target = default, RegionId Destination = default, ItemId Item = default);
public readonly record struct CommandCursor(EntityId Actor, ulong Sequence, long Tick);
public readonly record struct RecordedCommand(long SubmittedAfterTick, GameCommand Command);
public sealed record SimulationSnapshot(int RulesVersion, long Tick, uint RandomState, IReadOnlyList<EntityState> Entities,
	IReadOnlyList<CommandCursor> Inputs, IReadOnlyList<GameCommand> PendingCommands, CollisionGrid? Collision, WorldDefinition? World, EntityId WorldPlayer, QuestStage QuestState, IReadOnlyList<ItemState> Items, InventoryLayout? Inventory = null);

// A single owner advances authoritative state. No wall clock, rendering, I/O or callbacks in Step.
public sealed partial class GameSimulation
{
	public const int RulesVersion = 6, UnitsPerTile = 256, PositionLimit = 16777216;
	public const int MaxEntities = 1024, MaxPendingCommands = 4096, MaxCommandsPerTick = 128, CommandHorizon = 250;
	public const int MaxReplayCommands = 16384, MaxReplayTicks = 100000;
	private readonly EntityState[] entities;
	private readonly Dictionary<EntityId, int> indices = new();
	private readonly CommandCursor[] inputs;
	private readonly PriorityQueue<GameCommand, (long Tick, uint Actor, ulong Sequence)> commands = new();
	private readonly Dictionary<long, int> counts = new();
	private readonly SimulationEvent[] events;
	private int eventCount;
	public const int MaxCombatEntities = 128, AttackRange = 384, AggroRange = 1536, PlayerAttackInterval = 12, MonsterAttackInterval = 20, HitStunTicks = 3;
	public CollisionGrid? Collision { get; private set; }
	private readonly EntityId[] attacks;
	private readonly bool[] skillAttacks;
	private readonly SkillId[] castSkills;
	private readonly bool[] canAct, attacked;
	private SimulationRandom random;
	public long Tick { get; private set; }
	public uint RandomState => random.State;
	public int PendingCommands => commands.Count;
	// Spans are borrowed until the next Step; value snapshots below own their copies.
	public ReadOnlySpan<EntityState> Entities => entities;
	public ReadOnlySpan<SimulationEvent> Events => events.AsSpan(0, eventCount);
	public InventoryLayout Inventory { get; }
	public GameSimulation(uint seed, IEnumerable<EntityState> initialEntities, CollisionGrid? collision = null, WorldDefinition? world = null, InventoryLayout? inventory = null)
	{
		ArgumentNullException.ThrowIfNull(initialEntities); random = new(seed); Inventory = inventory ?? InventoryLayout.Default;
		entities = initialEntities.Take(MaxEntities + 1).OrderBy(e => e.Id.Value).ToArray();
		if (entities.Length is < 1 or > MaxEntities) throw new ArgumentException("Expected 1..1024 entities.", nameof(initialEntities));
		if (world is not null && collision is not null) throw new ArgumentException("Select either a single collision grid or a world.");
		World = world;
		if (world is not null)
		{
			for (int i = 0; i < entities.Length; i++)
				if (entities[i].Kind == EntityKind.Player)
				{
					if (worldPlayerIndex >= 0) throw new ArgumentException("The world slice supports exactly one player.");
					worldPlayerIndex = i;
				}
			if (worldPlayerIndex < 0) throw new ArgumentException("The world slice requires one player.");
			Collision = world.GetRegion(entities[worldPlayerIndex].Region).Collision;
		}
		else Collision = collision;
		if (world is null && collision is not null && entities.Length > MaxCombatEntities) throw new ArgumentException("Combat entity budget exceeded.");
		items = new ItemState[entities.Length];
		inputs = new CommandCursor[entities.Length]; events = new SimulationEvent[7 * entities.Length + MaxCommandsPerTick + (world is null ? 0 : 4)];
		attacks = new EntityId[entities.Length]; skillAttacks = new bool[entities.Length]; castSkills = new SkillId[entities.Length]; canAct = new bool[entities.Length]; attacked = new bool[entities.Length];
		for (int i = 0; i < entities.Length; i++)
		{
			var e = entities[i];
			if (e.Id.Value == 0 || e.Region.Value == 0 || !ValidPosition(e.Position) || !ValidDirection(e.MoveX, e.MoveY) || !indices.TryAdd(e.Id, i))
				throw new ArgumentException("Invalid entity, region, position, direction or duplicate ID.", nameof(initialEntities));
			if (!ValidResources(e) || !Enum.IsDefined(e.Kind) || !Enum.IsDefined(e.Mode) || e.MaxHealth is < 1 or > 100000 || e.Health < 0 || e.Health > e.MaxHealth ||
				e.AttackCooldown is < 0 or > MonsterAttackInterval || e.HitStun is < 0 or > HitStunTicks || e.Target != default || e.Mode != MonsterMode.Idle ||
				(e.Kind == EntityKind.Monster && Collision is null)) throw new ArgumentException("Invalid initial combat state.");
			var grid = world is null ? collision : world.GetRegion(e.Region).Collision;
			if (grid is not null && (e.Region != grid.Region || (e.IsAlive && !grid.CanOccupy(e.Position)))) throw new ArgumentException("Entity must spawn on known walkable cells in its region.");
			if (!e.IsAlive) entities[i] = e with { MoveX = 0, MoveY = 0, Mode = MonsterMode.Dead };
			inputs[i] = new(e.Id, 0, 0);
		}
		ValidateWorld();
		if (Collision is not null)
			for (int i = 0; i < entities.Length; i++) for (int j = 0; j < i; j++)
				if (entities[i].Region == entities[j].Region && entities[i].IsAlive && entities[j].IsAlive && Overlaps(entities[i].Position, entities[j].Position)) throw new ArgumentException("Living bodies overlap at spawn.");
	}
	public EntityState GetEntity(EntityId id) => entities[indices.TryGetValue(id, out int index) ? index : throw new ArgumentException("Unknown entity.", nameof(id))];
	public CommandResult Submit(GameCommand command)
	{
		if (command.Sequence == 0 || !Enum.IsDefined(command.Kind) ||
			(command.Kind == CommandKind.SetMove ? !ValidDirection(command.X, command.Y) : command.Kind == CommandKind.MoveItem ? command.X is < 0 or >= InventoryLayout.Width || command.Y is < 0 or >= InventoryLayout.Height : command.Kind == CommandKind.SelectSkill ? !Enum.IsDefined((SkillId)command.X) || command.Y != 0 : command.X != 0 || command.Y != 0) ||
			(command.Kind is not (CommandKind.Attack or CommandKind.CastSkill or CommandKind.Interact) && command.Target != default) ||
			(IsItemCommand(command.Kind) ? command.Item == default : command.Item != default)) return CommandResult.InvalidCommand;
		if (!indices.TryGetValue(command.Actor, out int index)) return CommandResult.UnknownActor;
		if (command.Region != entities[index].Region) return CommandResult.WrongRegion;
		if (!entities[index].IsAlive) return CommandResult.DeadActor;
		if (entities[index].Kind != EntityKind.Player) return CommandResult.NotPlayerControlled;
		if (command.Kind is (CommandKind.Attack or CommandKind.CastSkill) && (Collision is null || !indices.TryGetValue(command.Target, out int target) || entities[target].Kind == entities[index].Kind || entities[target].Region != command.Region)) return CommandResult.InvalidTarget;
		if (command.Kind == CommandKind.Interact && (World is null || !World.TryGetInteraction(command.Target, out var region, out _) || region != command.Region)) return CommandResult.InvalidTarget;
		if (IsItemCommand(command.Kind) && FindItem(command.Item) < 0) return CommandResult.InvalidTarget;
		if (command.Tick <= Tick) return CommandResult.ExpiredTick;
		if (command.Tick - Tick > CommandHorizon) return CommandResult.TooFarAhead;
		if (command.Sequence <= inputs[index].Sequence) return CommandResult.StaleSequence;
		if (command.Tick < inputs[index].Tick) return CommandResult.OutOfOrderTick;
		if (commands.Count >= MaxPendingCommands) return CommandResult.QueueFull;
		int count = counts.GetValueOrDefault(command.Tick);
		if (count >= MaxCommandsPerTick) return CommandResult.TickFull;
		commands.Enqueue(command, (command.Tick, command.Actor.Value, command.Sequence)); counts[command.Tick] = count + 1;
		inputs[index] = new(command.Actor, command.Sequence, command.Tick); return CommandResult.Accepted;
	}
	public void Step()
	{
		long nextTick = checked(Tick + 1); eventCount = 0; interaction = default;
		for (int i = 0; i < entities.Length; i++)
		{
			var e = entities[i]; bool active = IsActive(e); canAct[i] = active && e.IsAlive && e.HitStun == 0; attacked[i] = false; attacks[i] = default; skillAttacks[i] = false;
			if (active) entities[i] = AdvanceResources(e) with { AttackCooldown = Math.Max(0, e.AttackCooldown - 1), HitStun = Math.Max(0, e.HitStun - 1) };
		}
		while (commands.TryPeek(out var command, out _) && command.Tick == nextTick)
		{
			commands.Dequeue(); int index = indices[command.Actor]; var entity = entities[index];
			if (!entity.IsAlive || command.Region != entity.Region) continue;
			if (command.Kind == CommandKind.SetMove) entities[index] = entity with { MoveX = command.X, MoveY = command.Y };
			else if (command.Kind is CommandKind.Attack or CommandKind.CastSkill)
			{
				// One combat action per actor/tick: the last ordered action wins.
				attacks[index] = command.Target; skillAttacks[index] = command.Kind == CommandKind.CastSkill; castSkills[index] = entity.SelectedSkill;
			}
			else if (command.Kind == CommandKind.SelectSkill)
			{
				if (canAct[index]) entities[index] = entity with { SelectedSkill = (SkillId)command.X };
				else Emit(nextTick, SimulationEventKind.SkillFailed, entity, entity.Position, (int)SkillFailure.Interrupted);
			}
			else if (command.Kind == CommandKind.Interact) interaction = command.Target;
			else if (IsItemCommand(command.Kind)) ApplyItemCommand(index, command, nextTick);
			else Emit(nextTick, SimulationEventKind.Signaled, entity, entity.Position, random.NextInt(6));
		}
		counts.Remove(nextTick);
		if (Collision is not null)
		{
			Think(nextTick);
			for (int i = 0; i < entities.Length; i++) if (attacks[i] != default && entities[i].IsAlive) Attack(i, nextTick);
		}
		for (int i = 0; i < entities.Length; i++)
		{
			var e = entities[i]; if (!canAct[i] || attacked[i] || !e.IsAlive) continue;
			int distance = e.MoveX != 0 && e.MoveY != 0 ? 23 : 32;
			var wanted = new GamePosition(Math.Clamp(e.Position.X + e.MoveX * distance, -PositionLimit, PositionLimit), Math.Clamp(e.Position.Y + e.MoveY * distance, -PositionLimit, PositionLimit));
			var position = wanted;
			if (Collision is not null)
			{
				// Fixed X-then-Y sliding, with steps smaller than body/cell width: no corner cutting or tunneling.
				position = e.Position; var x = new GamePosition(wanted.X, position.Y);
				if (CanOccupy(i, x)) position = x;
				var y = new GamePosition(position.X, wanted.Y); if (CanOccupy(i, y)) position = y;
				if (wanted != position) Emit(nextTick, SimulationEventKind.Blocked, e, position);
			}
			if (position == e.Position) continue;
			entities[i] = e with { Position = position }; Emit(nextTick, SimulationEventKind.Moved, e, position);
		}
		Interact(nextTick);
		Tick = nextTick;
	}
	public SimulationSnapshot CaptureSnapshot() => new(RulesVersion, Tick, random.State, Array.AsReadOnly((EntityState[])entities.Clone()),
		Array.AsReadOnly((CommandCursor[])inputs.Clone()), Array.AsReadOnly(OrderedCommands()), Collision, World, WorldPlayer, QuestState, Array.AsReadOnly(Items.ToArray()), Inventory);
	public static GameSimulation Replay(uint seed, IEnumerable<EntityState> initialEntities, IReadOnlyList<RecordedCommand> trace, long targetTick, CollisionGrid? collision = null, WorldDefinition? world = null, InventoryLayout? inventory = null)
	{
		ArgumentNullException.ThrowIfNull(trace);
		if (targetTick is < 0 or > MaxReplayTicks || trace.Count > MaxReplayCommands) throw new ArgumentOutOfRangeException(nameof(targetTick), "Replay exceeds work budget.");
		var result = new GameSimulation(seed, initialEntities, collision, world, inventory);
		foreach (var entry in trace)
		{
			if (entry.SubmittedAfterTick < result.Tick || entry.SubmittedAfterTick > targetTick) throw new InvalidDataException("Replay submission times must be ordered and within target tick.");
			while (result.Tick < entry.SubmittedAfterTick) result.Step();
			var accepted = result.Submit(entry.Command);
			if (accepted != CommandResult.Accepted) throw new InvalidDataException($"Replay contains a rejected command: {accepted}.");
		}
		while (result.Tick < targetTick) result.Step();
		return result;
	}
	private GameCommand[] OrderedCommands() => commands.UnorderedItems.OrderBy(p => p.Priority).Select(p => p.Element).ToArray();
	// Canonical little-endian v6 state, including scheduling, world content, quest and item ownership.
	// Explicit diagnostic operation: allocates, so callers must not invoke it for every render frame.
	public string ComputeStateHash() => ComputeStateHash(RulesVersion);
	internal string ComputeStateHash(int version)
	{
		using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
		writer.Write(version); writer.Write(Tick); writer.Write(random.State); writer.Write(Collision is not null);
		if (Collision is { } grid)
		{
			writer.Write(grid.Region.Value); writer.Write(grid.Origin.X); writer.Write(grid.Origin.Y); writer.Write(grid.Width); writer.Write(grid.Height);
			foreach (var cell in grid.Cells) writer.Write((byte)cell);
		}
		writer.Write(entities.Length);
		for (int i = 0; i < entities.Length; i++)
		{
			var e = entities[i]; writer.Write(e.Id.Value); writer.Write(e.Region.Value); writer.Write(e.Position.X); writer.Write(e.Position.Y); writer.Write(e.MoveX); writer.Write(e.MoveY);
			writer.Write((int)e.Kind); writer.Write(e.Health); writer.Write(e.MaxHealth); writer.Write(e.AttackCooldown); writer.Write(e.HitStun); writer.Write((int)e.Mode); writer.Write(e.Target.Value);
			if (version >= 6) { writer.Write(e.Mana); writer.Write(e.MaxMana); writer.Write(e.SkillCooldown); writer.Write(e.ManaRecoveryTicks); writer.Write((int)e.SelectedSkill); }
			writer.Write(inputs[i].Sequence); writer.Write(inputs[i].Tick);
		}
		writer.Write(commands.Count);
		foreach (var c in OrderedCommands())
		{
			writer.Write(c.Tick); writer.Write(c.Sequence); writer.Write(c.Actor.Value); writer.Write(c.Region.Value); writer.Write((int)c.Kind); writer.Write(c.X); writer.Write(c.Y); writer.Write(c.Target.Value); writer.Write(c.Item.Value);
		}
		writer.Write(World is not null);
		if (World is { } world) { writer.Write(world.ContentHash); writer.Write(WorldPlayer.Value); writer.Write(ActiveRegion.Value); writer.Write((int)QuestState); }
		WriteItems(writer);
		if (version >= 5) writer.Write(Inventory.ContentHash);
		writer.Flush(); return Convert.ToHexStringLower(SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length)));
	}
	private static bool ValidDirection(int x, int y) => x is >= -1 and <= 1 && y is >= -1 and <= 1;
	private static bool ValidPosition(GamePosition p) => p.X is >= -PositionLimit and <= PositionLimit && p.Y is >= -PositionLimit and <= PositionLimit;
}
