using OpenD2.Core;

namespace OpenD2.Server;

public sealed record Account(Guid Id, string Name, string Salt, string Hash);
public sealed record Character(Guid Id, Guid Account, string Name, int Victories = 0);
public sealed record Member(Guid Character, uint Actor, long ClientSequence = 0);
public sealed record SavedGame(string StateHash, long Tick, uint Random, EntityState[] Entities, CommandCursor[] Inputs, GameCommand[] Pending, ItemState[] Items)
{
    public static SavedGame Capture(GameSimulation game)
    {
        var s = game.CaptureSnapshot();
        return new(game.ComputeStateHash(), s.Tick, s.RandomState, s.Entities.ToArray(), s.Inputs.ToArray(), s.PendingCommands.ToArray(), s.Items.ToArray());
    }
    public GameSimulation Restore()
    {
        var game = GameSimulation.Restore(new(GameSimulation.RulesVersion, Tick, Random, Entities, Inputs, Pending,
            Online.Arena.Grid(), null, default, QuestStage.Available, Items, InventoryLayout.Default));
        if (game.ComputeStateHash() != StateHash) throw new InvalidDataException("Room checksum mismatch.");
        return game;
    }
}
public sealed record SavedRoom(Guid Id, string Name, Guid Host, string Salt, string PasswordHash, Member[] Members, SavedGame? Game);
public sealed record Database(int Version, int RulesVersion, Account[] Accounts, Character[] Characters, SavedRoom[] Rooms, string? Mode = null);
public sealed class Rejected(int status, string message) : Exception(message) { public int Status => status; }
