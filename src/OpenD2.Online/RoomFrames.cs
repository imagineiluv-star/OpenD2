using System.Net;
using System.Text.Json.Serialization;
using OpenD2.Core;

namespace OpenD2.Online;

public sealed record EntityChange(int Index, EntityState Value);
public sealed record ItemChange(int Index, ItemState Value);
public sealed record RoomDelta(Guid Room, long Tick, long NextSequence, string StateHash, EntityChange[] Entities, ItemChange[] Items);
public sealed record RoomFrame(long Sequence, long BaseSequence = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RoomView? State = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RoomDelta? Delta = null,
    int Status = 200, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error = null);

// One previous snapshot per connection. Array membership/order changes use a full snapshot;
// index patches preserve the authoritative order, including room-local imported item IDs.
public static class RoomFrames
{
    public const string Protocol = "opend2.room.v2";
    public const int FullInterval = 125; // At most 124 dependent frames between full snapshots.
    public const int MaxItems = 2 * GameSimulation.MaxEntities + GameSimulation.MaxCarriedItems;

    public static RoomFrame Create(RoomView current, RoomView? previous, long sequence)
    {
        if (sequence <= 0) throw new ArgumentOutOfRangeException(nameof(sequence));
        if (previous is null || (sequence - 1) % FullInterval == 0 ||
            current.Id != previous.Id || current.Name != previous.Name || current.Started != previous.Started ||
            current.Host != previous.Host || current.Actor != previous.Actor || !current.Members.SequenceEqual(previous.Members) ||
            !current.Entities.Select(e => e.Id).SequenceEqual(previous.Entities.Select(e => e.Id)) ||
            !current.Items.Select(i => i.Id).SequenceEqual(previous.Items.Select(i => i.Id))) return new(sequence, State: current);
        var entities = Enumerable.Range(0, current.Entities.Length).Where(i => current.Entities[i] != previous.Entities[i])
            .Select(i => new EntityChange(i, current.Entities[i])).ToArray();
        var items = Enumerable.Range(0, current.Items.Length).Where(i => current.Items[i] != previous.Items[i])
            .Select(i => new ItemChange(i, current.Items[i])).ToArray();
        // Avoid patch overhead when most records changed (including tiny lobby snapshots).
        int records = current.Entities.Length + current.Items.Length;
        if (records == 0 || (entities.Length + items.Length) * 2 > records)
            return new(sequence, State: current);
        return new(sequence, sequence - 1, Delta: new(current.Id, current.Tick, current.NextSequence, current.StateHash, entities, items));
    }

    public static RoomView Apply(RoomFrame frame, RoomView? previous, long previousSequence, Guid room)
    {
        if (previousSequence == long.MaxValue || frame.Sequence != previousSequence + 1 || frame.Sequence <= 0)
            throw new InvalidDataException("Room frame sequence gap or replay. Reconnect to the room.");
        if (frame.Status != 200)
        {
            if (frame.Status is < 400 or > 599 || frame.State is not null || frame.Delta is not null || frame.BaseSequence != 0)
                throw new InvalidDataException("Invalid terminal room frame.");
            throw new HttpRequestException(frame.Error ?? "Room access ended.", null, (HttpStatusCode)frame.Status);
        }
        if (frame.Error is not null) throw new InvalidDataException("Successful room frame contains an error.");
        RoomView next;
        if (frame.State is { } full && frame.Delta is null && frame.BaseSequence == 0) next = full;
        else if (frame.State is null && frame.Delta is { } delta && previous is not null && frame.BaseSequence == previousSequence && delta.Room == room)
        {
            if (delta.Entities is null || delta.Items is null || delta.Entities.Length > previous.Entities.Length || delta.Items.Length > previous.Items.Length)
                throw new InvalidDataException("Invalid room delta budget.");
            var entities = previous.Entities.ToArray(); var items = previous.Items.ToArray();
            int last = -1;
            foreach (var change in delta.Entities)
            {
                if (change is null || change.Index <= last || change.Index >= entities.Length || change.Value.Id != entities[change.Index].Id)
                    throw new InvalidDataException("Invalid entity patch index or identity.");
                entities[last = change.Index] = change.Value;
            }
            last = -1;
            foreach (var change in delta.Items)
            {
                if (change is null || change.Index <= last || change.Index >= items.Length || change.Value.Id != items[change.Index].Id)
                    throw new InvalidDataException("Invalid item patch index or identity.");
                items[last = change.Index] = change.Value;
            }
            next = previous with { Tick = delta.Tick, NextSequence = delta.NextSequence, StateHash = delta.StateHash, Entities = entities, Items = items };
        }
        else throw new InvalidDataException("Room delta has no matching full baseline. Reconnect to the room.");
        Validate(next, room);
        if (previous is not null && (next.Tick < previous.Tick || next.NextSequence < previous.NextSequence || (previous.Started && !next.Started)))
            throw new InvalidDataException("Room state moved backwards.");
        return next;
    }

    private static void Validate(RoomView state, Guid room)
    {
        if (state.Id != room || state.Actor == 0 || state.Tick < 0 || state.NextSequence <= 0 || state.Name is not { Length: <= 128 } ||
            state.Members is not { Length: >= 1 and <= 4 } || state.Entities is null || state.Entities.Length > GameSimulation.MaxEntities ||
            state.Items is null || state.Items.Length > MaxItems || state.StateHash is null ||
            (state.Started ? state.StateHash.Length != 64 || !state.StateHash.All(char.IsAsciiHexDigit) : state.StateHash.Length != 0) ||
            state.Members.Any(m => m is null || m.Character == Guid.Empty || m.Actor == 0 || m.Name is not { Length: <= 128 }) ||
            state.Members.Select(m => m.Character).Distinct().Count() != state.Members.Length ||
            state.Members.Select(m => m.Actor).Distinct().Count() != state.Members.Length || !state.Members.Any(m => m.Actor == state.Actor) ||
            state.Entities.Any(e => e.Id.Value == 0) || state.Entities.Select(e => e.Id).Distinct().Count() != state.Entities.Length ||
            state.Items.Any(i => i.Id.Value == 0) || state.Items.Select(i => i.Id).Distinct().Count() != state.Items.Length ||
            (state.Started && !state.Entities.Any(e => e.Id.Value == state.Actor)))
            throw new InvalidDataException("Invalid full room state or reconstruction budget.");
    }
}
