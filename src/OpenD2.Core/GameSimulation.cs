using System.Security.Cryptography;

namespace OpenD2.Core;

public readonly record struct RegionId(uint Value);
public readonly record struct EntityId(uint Value);
public readonly record struct GamePosition(int X, int Y);
public readonly record struct EntityState(EntityId Id, RegionId Region, GamePosition Position, int MoveX = 0, int MoveY = 0);
public enum CommandKind { SetMove, Signal }
public readonly record struct GameCommand(long Tick, ulong Sequence, EntityId Actor, RegionId Region, CommandKind Kind, int X = 0, int Y = 0);
public enum CommandResult { Accepted, InvalidCommand, UnknownActor, WrongRegion, ExpiredTick, TooFarAhead, StaleSequence, OutOfOrderTick, TickFull, QueueFull }
public enum SimulationEventKind { Moved, Signaled }
public readonly record struct SimulationEvent(long Tick, SimulationEventKind Kind, EntityId Actor, RegionId Region, GamePosition From, GamePosition To, int Value = 0);
public readonly record struct CommandCursor(EntityId Actor, ulong Sequence, long Tick);
public readonly record struct RecordedCommand(long SubmittedAfterTick, GameCommand Command);
public sealed record SimulationSnapshot(int RulesVersion, long Tick, uint RandomState, IReadOnlyList<EntityState> Entities,
	IReadOnlyList<CommandCursor> Inputs, IReadOnlyList<GameCommand> PendingCommands);

// A single owner advances authoritative state. No wall clock, rendering, I/O or callbacks in Step.
public sealed class GameSimulation
{
	public const int RulesVersion = 1, UnitsPerTile = 256, PositionLimit = 16777216;
	public const int MaxEntities = 1024, MaxPendingCommands = 4096, MaxCommandsPerTick = 128, CommandHorizon = 250;
	public const int MaxReplayCommands = 16384, MaxReplayTicks = 100000;
	private readonly EntityState[] entities;
	private readonly Dictionary<EntityId, int> indices = new();
	private readonly CommandCursor[] inputs;
	private readonly PriorityQueue<GameCommand, (long Tick, uint Actor, ulong Sequence)> commands = new();
	private readonly Dictionary<long, int> counts = new();
	private readonly SimulationEvent[] events;
	private int eventCount;
	private SimulationRandom random;
	public long Tick { get; private set; }
	public uint RandomState => random.State;
	public int PendingCommands => commands.Count;
	// Spans are borrowed until the next Step; value snapshots below own their copies.
	public ReadOnlySpan<EntityState> Entities => entities;
	public ReadOnlySpan<SimulationEvent> Events => events.AsSpan(0, eventCount);
	public GameSimulation(uint seed, IEnumerable<EntityState> initialEntities)
	{
		ArgumentNullException.ThrowIfNull(initialEntities); random = new(seed);
		entities = initialEntities.Take(MaxEntities + 1).OrderBy(e => e.Id.Value).ToArray();
		if (entities.Length is < 1 or > MaxEntities) throw new ArgumentException("Expected 1..1024 entities.", nameof(initialEntities));
		inputs = new CommandCursor[entities.Length]; events = new SimulationEvent[entities.Length + MaxCommandsPerTick];
		for (int i = 0; i < entities.Length; i++)
		{
			var e = entities[i];
			if (e.Id.Value == 0 || e.Region.Value == 0 || !ValidPosition(e.Position) || !ValidDirection(e.MoveX, e.MoveY) || !indices.TryAdd(e.Id, i))
				throw new ArgumentException("Invalid entity, region, position, direction or duplicate ID.", nameof(initialEntities));
			inputs[i] = new(e.Id, 0, 0);
		}
	}
	public EntityState GetEntity(EntityId id) => entities[indices.TryGetValue(id, out int index) ? index : throw new ArgumentException("Unknown entity.", nameof(id))];
	public CommandResult Submit(GameCommand command)
	{
		if (command.Sequence == 0 || !Enum.IsDefined(command.Kind) ||
			(command.Kind == CommandKind.SetMove ? !ValidDirection(command.X, command.Y) : command.X != 0 || command.Y != 0)) return CommandResult.InvalidCommand;
		if (!indices.TryGetValue(command.Actor, out int index)) return CommandResult.UnknownActor;
		if (command.Region != entities[index].Region) return CommandResult.WrongRegion;
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
		long nextTick = checked(Tick + 1); eventCount = 0;
		while (commands.TryPeek(out var command, out _) && command.Tick == nextTick)
		{
			commands.Dequeue(); int index = indices[command.Actor]; var entity = entities[index];
			if (command.Kind == CommandKind.SetMove) entities[index] = entity with { MoveX = command.X, MoveY = command.Y };
			else events[eventCount++] = new(nextTick, SimulationEventKind.Signaled, entity.Id, entity.Region, entity.Position, entity.Position, random.NextInt(6));
		}
		counts.Remove(nextTick);
		for (int i = 0; i < entities.Length; i++)
		{
			var e = entities[i]; int distance = e.MoveX != 0 && e.MoveY != 0 ? 23 : 32;
			var position = new GamePosition(Math.Clamp(e.Position.X + e.MoveX * distance, -PositionLimit, PositionLimit), Math.Clamp(e.Position.Y + e.MoveY * distance, -PositionLimit, PositionLimit));
			if (position == e.Position) continue;
			entities[i] = e with { Position = position };
			events[eventCount++] = new(nextTick, SimulationEventKind.Moved, e.Id, e.Region, e.Position, position);
		}
		Tick = nextTick;
	}
	public SimulationSnapshot CaptureSnapshot() => new(RulesVersion, Tick, random.State, Array.AsReadOnly((EntityState[])entities.Clone()),
		Array.AsReadOnly((CommandCursor[])inputs.Clone()), Array.AsReadOnly(OrderedCommands()));
	public static GameSimulation Replay(uint seed, IEnumerable<EntityState> initialEntities, IReadOnlyList<RecordedCommand> trace, long targetTick)
	{
		ArgumentNullException.ThrowIfNull(trace);
		if (targetTick is < 0 or > MaxReplayTicks || trace.Count > MaxReplayCommands) throw new ArgumentOutOfRangeException(nameof(targetTick), "Replay exceeds work budget.");
		var result = new GameSimulation(seed, initialEntities);
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
	// Canonical little-endian v1 state, including scheduling watermarks and queued commands.
	// Explicit diagnostic operation: allocates, so callers must not invoke it for every render frame.
	public string ComputeStateHash()
	{
		using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
		writer.Write(RulesVersion); writer.Write(Tick); writer.Write(random.State); writer.Write(entities.Length);
		for (int i = 0; i < entities.Length; i++)
		{
			var e = entities[i]; writer.Write(e.Id.Value); writer.Write(e.Region.Value); writer.Write(e.Position.X); writer.Write(e.Position.Y); writer.Write(e.MoveX); writer.Write(e.MoveY);
			writer.Write(inputs[i].Sequence); writer.Write(inputs[i].Tick);
		}
		writer.Write(commands.Count);
		foreach (var c in OrderedCommands())
		{
			writer.Write(c.Tick); writer.Write(c.Sequence); writer.Write(c.Actor.Value); writer.Write(c.Region.Value); writer.Write((int)c.Kind); writer.Write(c.X); writer.Write(c.Y);
		}
		writer.Flush(); return Convert.ToHexStringLower(SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length)));
	}
	private static bool ValidDirection(int x, int y) => x is >= -1 and <= 1 && y is >= -1 and <= 1;
	private static bool ValidPosition(GamePosition p) => p.X is >= -PositionLimit and <= PositionLimit && p.Y is >= -PositionLimit and <= PositionLimit;
}
