using OpenD2.Online;

namespace OpenD2.Client;

public partial class OnlinePanel
{
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
