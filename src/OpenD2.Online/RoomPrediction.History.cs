using OpenD2.Core;

namespace OpenD2.Online;

public sealed partial class RoomPrediction
{
    public const int MaxPendingMoves = 16;
    private readonly record struct PendingMove(long Sequence, long Tick, int X, int Y, double Sent);
    private readonly List<PendingMove> history = new(MaxPendingMoves);
    private long replayTick;
    private double replayTime;
    public int PendingMoves => history.Count;

    private void ClearHistory() { history.Clear(); replayTick = 0; replayTime = 0; }
    private bool HistoryFresh(double now) => now >= history[^1].Sent && now - history[0].Sent < MaxLead;
    private bool Remember(long inputSequence, int x, int y, double now)
    {
        if (history.Count != 0 && inputSequence <= history[^1].Sequence) return false;
        if (state!.Tick > long.MaxValue - 4 || history.Count == MaxPendingMoves ||
            (history.Count != 0 && !HistoryFresh(now)))
        { Cancel(); return false; }
        if (history.Count == 0)
        {
            replayTick = state.Tick;
            replayTime = IsAnimating(now) ? received : now;
        }
        double steps = (now - replayTime) * FixedTickClock.TicksPerSecond;
        if (steps >= 4) { Cancel(); return false; }
        // Tick is the start of the predicted step. The receipt replaces this estimate
        // with the actual queue tick; multiple moves in one server tick use the last one.
        long tick = replayTick + (long)Math.Floor(steps + 0.0000001);
        history.Add(new(inputSequence, tick, x, y, now));
        return true;
    }
    private void UpdateHistory(RoomView next, double now, bool continuous)
    {
        if (history.Count == 0) return;
        if (!continuous || next.ProcessedSequence is not { } processed)
        { Cancel(); return; }
        int consumed = 0;
        while (consumed < history.Count && history[consumed].Sequence <= processed) consumed++;
        if (consumed != 0) history.RemoveRange(0, consumed);
        if (history.Count == 0) { pending = false; ClearHistory(); return; }
        if (!HistoryFresh(now)) { Cancel(); return; }
        if (next.Tick == state!.Tick) return; // Metadata cannot restart the replay clock.
        double lead = replayTick - next.Tick + (now - replayTime) * FixedTickClock.TicksPerSecond;
        if (lead > 4) { Cancel(); return; }
        replayTick = next.Tick;
        replayTime = now - Math.Max(0, lead) / FixedTickClock.TicksPerSecond;
    }
    private void AcceptHistory(InputReceipt receipt)
    {
        if (state is null || receipt.Room != state.Id || receipt.Actor != state.Actor || receipt.Tick < 0) return;
        int index = history.FindIndex(move => move.Sequence < long.MaxValue && move.Sequence + 1 == receipt.NextSequence);
        if (index < 0) return; // Late receipts cannot restore consumed entries.
        history[index] = history[index] with { Tick = receipt.Tick };
    }
    private DisplayPosition Replay(double now)
    {
        var position = self.Position;
        double steps = Math.Clamp((now - replayTime) * FixedTickClock.TicksPerSecond, 0, 4);
        int x = self.MoveX, y = self.MoveY, cursor = 0;
        long start = state!.Tick;
        // At most four fixed steps and sixteen inputs. Always rebuild from authority;
        // previously displayed coordinates and corrections never feed the simulation.
        for (int i = 0; i < 4 && steps > 0; i++, steps -= 1)
        {
            while (cursor < history.Count)
            {
                start = Math.Max(start, history[cursor].Tick);
                if (start > state.Tick + i) break;
                x = history[cursor].X; y = history[cursor++].Y;
            }
            position = Step(position, x, y, steps);
        }
        return new(position.X, position.Y);
    }
}
