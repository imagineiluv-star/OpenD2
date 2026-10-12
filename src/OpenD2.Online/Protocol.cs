using OpenD2.Core;

namespace OpenD2.Online;

public sealed record ServerInfo(int Protocol, int Rules, int RoomStream = 0, string Mode = "realm", int CharacterFileVersion = 0, int InputReceipt = 0, int RoomDelta = 0, int InputBatch = 0, int RoomInterest = 0, int RoomInputProgress = 0, int RoomWorld = 0, int CharacterProgress = 0, int MonsterKinds = 0, string? Tables = null);
public sealed record RoomUpdate(RoomView? State, int Status = 200, string? Error = null);
public sealed record Credentials(string Username, string Password);
public sealed record LoginResult(string Token, string Username);
// Class is a CharacterClass name (for example "Amazon"). Omitted: a legacy character without progression.
public sealed record NameRequest(string Name,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Class = null);
public sealed record StatRequest(string Stat);
public sealed record SkillRequest(int Skill);
// One entry of the server's skill table (all classes). Requires lists skill IDs that need at least one point first.
// One kind of the server's monster table: Name is the table's name key, Experience what a kill pays each eligible member.
public sealed record MonsterInfo(string Code, string Name, int Level, int Experience);
public sealed record SkillInfo(int Id, string Name, string Class, int RequiredLevel, int MaxLevel, int[] Requires);
// Per-character count of finished shared world quests, keyed by world content ID (never by hash).
// Quests is omitted from the wire until a character has a record, so earlier clients see the same JSON.
public sealed record QuestRecord(string World, int Completions);
// One rule set for realm.json and Open character files, so a record that loads in one place loads in the other.
public static class QuestRecords
{
    public const int MaxRecords = 8, MaxCompletions = 1_000_000;
    public static bool IsValid(QuestRecord[]? quests) => quests is not null && quests.Length <= MaxRecords &&
        quests.All(q => q is not null && q.World is not null && System.Text.RegularExpressions.Regex.IsMatch(q.World, "\\A[a-z0-9-]{1,64}\\z") && q.Completions is >= 1 and <= MaxCompletions) &&
        quests.Select(q => q.World).Distinct().Count() == quests.Length;
}
public sealed record CharacterInfo(Guid Id, string Name, int Victories,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] QuestRecord[]? Quests = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] CharacterProgress? Progress = null);
public sealed record WorldContent(string Id, string Hash);
public sealed record RoomWorldState(WorldContent Content, QuestProgress Quest, long RegionEpoch);
public sealed record RoomRequest(Guid Character, string Name, string Password,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] WorldContent? World = null);
public sealed record JoinRequest(Guid Character, string Password, [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Invitation = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] WorldContent? World = null)
{
    public override string ToString() => $"Join request for {Character}; credentials omitted";
}
public sealed record RoomInvitation(string Token, DateTimeOffset ExpiresAt)
{
    public override string ToString() => $"Room invitation expires {ExpiresAt:O}; token omitted";
}
public sealed record RoomInfo(Guid Id, string Name, int Players, bool Started, bool Locked,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] WorldContent? World = null);
public sealed record MemberInfo(Guid Character, string Name, uint Actor);
public sealed record InputRequest(long Sequence, CommandKind Kind, int X = 0, int Y = 0, uint Target = 0, ulong Item = 0,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] uint? Region = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] long? RegionEpoch = null);
public sealed record InputBatchRequest(InputRequest[] Inputs);
// Acceptance queues a command for a later tick; it is not a simulation result.
public sealed record InputReceipt(Guid Room, uint Actor, long NextSequence, long Tick);
// ProcessedSequence is v4-only: commands at or below it were consumed or discarded,
// not necessarily successful. Null means this connection has no explicit progress cursor.
public sealed record RoomView(Guid Id, string Name, bool Started, bool Host, uint Actor, long NextSequence,
    long Tick, string StateHash, MemberInfo[] Members, EntityState[] Entities, ItemState[] Items,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] long? ProcessedSequence = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] RoomWorldState? World = null,
    // The recipient's own experience earned in this room so far (settled into the character when the host closes it). Null for a
    // character without a class, so legacy frames keep their shape. Never carries another member's value.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] uint? Experience = null);
public sealed record ApiError(string Error);

public static class Arena
{
    public const int Width = 24, Height = 18;
    // The two monsters of the Arena and what they are (see MonsterRoster).
    public static MonsterRoster Roster { get; } = new([(100, "preview-grunt"), (101, "preview-brute")]);
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
