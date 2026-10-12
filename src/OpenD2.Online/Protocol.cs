using OpenD2.Core;

namespace OpenD2.Online;

public sealed record ServerInfo(int Protocol, int Rules, int RoomStream = 0, string Mode = "realm", int CharacterFileVersion = 0, int InputReceipt = 0, int RoomDelta = 0, int InputBatch = 0, int RoomInterest = 0, int RoomInputProgress = 0);
public sealed record RoomUpdate(RoomView? State, int Status = 200, string? Error = null);
public sealed record Credentials(string Username, string Password);
public sealed record LoginResult(string Token, string Username);
public sealed record NameRequest(string Name);
public sealed record CharacterInfo(Guid Id, string Name, int Victories);
public sealed record RoomRequest(Guid Character, string Name, string Password);
public sealed record JoinRequest(Guid Character, string Password, [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Invitation = null)
{
    public override string ToString() => $"Join request for {Character}; credentials omitted";
}
public sealed record RoomInvitation(string Token, DateTimeOffset ExpiresAt)
{
    public override string ToString() => $"Room invitation expires {ExpiresAt:O}; token omitted";
}
public sealed record RoomInfo(Guid Id, string Name, int Players, bool Started, bool Locked);
public sealed record MemberInfo(Guid Character, string Name, uint Actor);
public sealed record InputRequest(long Sequence, CommandKind Kind, int X = 0, int Y = 0, uint Target = 0, ulong Item = 0);
public sealed record InputBatchRequest(InputRequest[] Inputs);
// Acceptance queues a command for a later tick; it is not a simulation result.
public sealed record InputReceipt(Guid Room, uint Actor, long NextSequence, long Tick);
// ProcessedSequence is v4-only: commands at or below it were consumed or discarded,
// not necessarily successful. Null means this connection has no explicit progress cursor.
public sealed record RoomView(Guid Id, string Name, bool Started, bool Host, uint Actor, long NextSequence,
    long Tick, string StateHash, MemberInfo[] Members, EntityState[] Entities, ItemState[] Items,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] long? ProcessedSequence = null);
public sealed record ApiError(string Error);

public static class Arena
{
    public const int Width = 24, Height = 18;
    public static CollisionGrid Grid()
    {
        var cells = Enumerable.Repeat(CollisionCell.Open, Width * Height).ToArray();
        for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
            if (x == 0 || y == 0 || x == Width - 1 || y == Height - 1 || (x == 12 && y is >= 6 and <= 11)) cells[y * Width + x] = CollisionCell.Blocked;
        return new(new(1), Width, Height, cells);
    }
    public static GameSimulation Create(int players) => new(42,
        Enumerable.Range(1, players).Select(i => new EntityState(new((uint)i), new(1), new(768, 768 + (i - 1) * 512)))
        .Concat([new EntityState(new(100), new(1), new(1792, 768), Kind: EntityKind.Monster, Health: 60, MaxHealth: 60),
                 new EntityState(new(101), new(1), new(4352, 3328), Kind: EntityKind.Monster, Health: 60, MaxHealth: 60)]), Grid());
}
