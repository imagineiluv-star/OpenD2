using OpenD2.Online;
using OpenD2.Core;

namespace OpenD2.Client;

public partial class OnlinePanel
{
    private void CheckRoomPresentation()
    {
        var entities = Arena.Create(2).Entities.ToArray();
        var first = new RoomView(Guid.NewGuid(), "Presentation", true, true, 1, 1, 1, "authoritative", [], entities, []);
        var next = first with { Tick = 2, Entities = entities.Select(e => e.Id.Value is 1 or 2 ? e with { Position = new(e.Position.X + 32, e.Position.Y) } : e).ToArray() };
        arena.SetState(first, 1); arena.SetState(next, 1.04);
        var remote = arena.Display(next.Entities[1], 1.06); var local = arena.Display(next.Entities[0], 1.06);
        if (Math.Abs(remote.X - (entities[1].Position.X + 16)) > 0.000001 || local.X != next.Entities[0].Position.X ||
            !ReferenceEquals(arena.State, next) || arena.State.StateHash != "authoritative" || entities[1].Position.X != 768)
            throw new Exception("Display interpolation changed authoritative state or local position.");
        arena.SetState(first, 1.07);
        if (!ReferenceEquals(arena.State, next)) throw new Exception("Display accepted an older tick.");
        arena.State = null; arena.SetState(next, 2);
        if (arena.Display(next.Entities[1], 2).X != next.Entities[1].Position.X || arena.PresentationTransitions != 0)
            throw new Exception("Reconnect retained display interpolation.");
        arena.State = null;
        Godot.GD.Print("OPEND2_ROOM_PRESENTATION_UI_PASS");
    }
    private void CheckInputReceiptOrdering()
    {
        // Deterministic ordering probe before login, with no network connection.
        state = new(Guid.NewGuid(), "Ordering", false, true, 1, 1, 0, "", [], [], []);
        var original = state;
        nextInputSequence = 1;
        ApplyInputReceipt(new(state.Id, 1, 3, 0));
        if (state != original || nextInputSequence != 3) throw new Exception("Receipt changed rendered state.");
        Apply(state with { Tick = 1, NextSequence = 2 });
        if (state.Tick != 1 || nextInputSequence != 3) throw new Exception("Receipt suppressed a fresh pushed state or rolled back the cursor.");
        Apply(state with { Tick = 2, NextSequence = 4 });
        var newest = state;
        ApplyInputReceipt(new(state.Id, 1, 3, 0));
        ApplyInputReceipt(new(Guid.NewGuid(), 1, 99, 2));
        ApplyInputReceipt(new(state.Id, 2, 99, 2));
        Apply(state with { Tick = 1, NextSequence = 2 });
        if (state != newest || nextInputSequence != 4) throw new Exception("Late/foreign receipt or stale stream corrupted input ordering.");
        ClearRoom();
        if (nextInputSequence != 0) throw new Exception("Leaving a room retained its input cursor.");
        Godot.GD.Print("OPEND2_INPUT_RECEIPT_ORDERING_PASS");
    }
}
