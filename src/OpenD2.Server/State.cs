using System.Text.Json.Serialization;
using OpenD2.Core;
using OpenD2.Online;

namespace OpenD2.Server;

public sealed record Account(Guid Id, string Name, string Salt, string Hash);
// Progress is null for a character created without a class: it keeps the original fixed resources and cannot earn experience.
public sealed record Character(Guid Id, Guid Account, string Name, int Victories = 0, CarriedItem[]? Inventory = null, QuestRecord[]? Quests = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CharacterProgress? Progress = null);
// QuestCredit: this member completed their own quest (their own turn-in). It is room state until the host closes
// the room, so it is saved with the game and cannot outlive the completion that earned it.
// Experience: earned in this room by a character with a class. Like items and quest credit it is room state until the host closes the room.
public sealed record Member(Guid Character, uint Actor, long ClientSequence = 0, long RegionEpoch = 0, bool QuestCredit = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] uint Experience = 0);
public sealed record SavedGame(string StateHash, long Tick, uint Random, EntityState[] Entities, CommandCursor[] Inputs, GameCommand[] Pending, ItemState[] Items,
    string? WorldHash = null, QuestStage Quest = QuestStage.Available,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] QuestStage[]? PlayerQuests = null)
{
    public static SavedGame Capture(GameSimulation game)
    {
        var s = game.CaptureSnapshot();
        return new(game.ComputeStateHash(), s.Tick, s.RandomState, s.Entities.ToArray(), s.Inputs.ToArray(), s.PendingCommands.ToArray(), s.Items.ToArray(), s.World?.ContentHash, s.QuestState, s.PlayerQuests?.ToArray());
    }
    public GameSimulation Restore(int rules = GameSimulation.RulesVersion, WorldContent? content = null)
    {
        var world = content is null ? null : CooperativeWorld.Resolve(content);
        if (WorldHash != world?.ContentHash || (world is not null && rules != GameSimulation.RulesVersion))
            throw new InvalidDataException("Saved room world content does not match.");
        var game = GameSimulation.RestoreCompatible(new(rules, Tick, Random, Entities, Inputs, Pending,
            world is null ? Arena.Grid() : null, world, world is null ? default : new(1), Quest, Items, InventoryLayout.Default, PlayerQuests), StateHash);
        return game;
    }
}
public sealed record SavedRoom(Guid Id, string Name, Guid Host, string Salt, string PasswordHash, Member[] Members, SavedGame? Game, WorldContent? World = null);
public sealed record Database(int Version, int RulesVersion, Account[] Accounts, Character[] Characters, SavedRoom[] Rooms, string? Mode = null);
public sealed class Rejected(int status, string message) : Exception(message) { public int Status => status; }
