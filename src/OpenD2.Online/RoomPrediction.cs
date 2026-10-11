using OpenD2.Core;

namespace OpenD2.Online;

// Bounded, render-only movement. Receipts acknowledge queueing, never simulation results.
// The UI owns this together with RoomPresentation; snapshots remain authoritative.
public sealed class RoomPrediction(CollisionGrid grid)
{
    public const double MaxLead = 4.0 / FixedTickClock.TicksPerSecond;
    public const double CorrectionDuration = 2.0 / FixedTickClock.TicksPerSecond;
    public const int MaxCorrection = CollisionGrid.BodyRadius;
    private DisplayPosition correction;
    private double corrected, moveStarted;
    private RoomView? state;
    private EntityState self;
    private double received, sent;
    private long sequence, queuedTick = -1;
    private int moveX, moveY;
    private bool pending, suspended;
    public long Predictions { get; private set; }

    public void Reset() { state = null; pending = suspended = false; queuedTick = -1; correction = default; Predictions = 0; }
    public void Push(RoomView next, double now)
    {
        CheckTime(now);
        ArgumentNullException.ThrowIfNull(next);
        if (next.Entities is null || next.Entities.Length > GameSimulation.MaxEntities) throw new ArgumentException("Entity budget exceeded.", nameof(next));
        var actor = next.Entities.FirstOrDefault(e => e.Id.Value == next.Actor);
        bool same = state is { Started: true } && next.Started && state.Id == next.Id && state.Actor == next.Actor;
        if (same && next.Tick < state!.Tick) return;
        bool continuous = same && now >= received && now - received <= MaxLead && next.Tick - state!.Tick <= 4 &&
            actor.Region == self.Region && actor.Kind == self.Kind && actor.IsAlive && self.IsAlive &&
            actor.Health >= self.Health && (next.Tick != state!.Tick || actor.Position == self.Position) && Math.Abs((long)actor.Position.X - self.Position.X) <= GameSimulation.UnitsPerTile &&
            Math.Abs((long)actor.Position.Y - self.Position.Y) <= GameSimulation.UnitsPerTile;
        long oldTick = state?.Tick ?? -1;
        bool wasMoving = IsAnimating(now);
        var before = Position(self, now);
        int previousX = pending ? moveX : self.MoveX, previousY = pending ? moveY : self.MoveY;
        if (!continuous) { pending = false; queuedTick = -1; }
        if (!continuous || state is null || next.Tick > state.Tick) suspended = !continuous;
        if (pending && next.NextSequence > sequence)
        {
            if (queuedTick >= 0 && next.Tick > queuedTick) pending = false;
            else if (queuedTick < 0) queuedTick = next.Tick;
        }
        // Same-tick metadata/receipt projections cannot extend the prediction horizon.
        if (state is null || !same || next.Tick != state.Tick || now < received) received = now;
        state = next; self = actor;
        if (!continuous || !CanMove || suspended || actor.MoveX != previousX || actor.MoveY != previousY) correction = default;
        else if (next.Tick > oldTick)
        {
            correction = default;
            var error = new DisplayPosition(before.X - actor.Position.X, before.Y - actor.Position.Y);
            if (wasMoving && (actor.MoveX != 0 || actor.MoveY != 0) && actor.MoveX == previousX && actor.MoveY == previousY &&
                Math.Abs(error.X) <= MaxCorrection && Math.Abs(error.Y) <= MaxCorrection &&
                ClearCorrectionPath(new(actor.Position.X, actor.Position.Y), before))
            { correction = error; corrected = now; }
        }
        // Same-tick projections preserve the original correction deadline.
    }
    public void Move(long inputSequence, int x, int y, double now)
    {
        CheckTime(now);
        if (x is < -1 or > 1 || y is < -1 or > 1) throw new ArgumentOutOfRangeException(nameof(x));
        if (state is not { Started: true } || inputSequence < state.NextSequence || inputSequence == long.MaxValue || !CanMove || now < received || now - received > MaxLead) return;
        bool continuing = IsAnimating(now) && x == (pending ? moveX : self.MoveX) && y == (pending ? moveY : self.MoveY);
        if (!continuing) correction = default;
        moveStarted = continuing ? (pending ? moveStarted : received) : now;
        sequence = inputSequence; moveX = x; moveY = y; sent = now;
        queuedTick = -1; pending = true; suspended = false; Predictions++;
    }
    public void Accept(InputReceipt receipt)
    {
        if (!pending || state is null || receipt.Room != state.Id || receipt.Actor != state.Actor || receipt.NextSequence != sequence + 1) return;
        // A WSS snapshot may already have applied the command before HTTP returns.
        queuedTick = receipt.Tick;
        if (state.NextSequence >= receipt.NextSequence && state.Tick > receipt.Tick) pending = false;
    }
    public void Cancel() { pending = false; suspended = true; queuedTick = -1; correction = default; }
    private bool CanMove => self.Id.Value != 0 && self.IsAlive && self.HitStun == 0 && self.AttackCooldown == 0 && self.Region == grid.Region;
    public bool IsAnimating(double now)
    {
        CheckTime(now);
        return state is { Started: true } && !suspended && CanMove && now >= received && now - received < MaxLead &&
            (!pending || now >= sent && now - sent < MaxLead) && (pending ? moveX != 0 || moveY != 0 : self.MoveX != 0 || self.MoveY != 0);
    }
    public DisplayPosition Position(EntityState entity, double now)
    {
        CheckTime(now);
        var position = entity.Position;
        if (entity != self || !IsAnimating(now)) return new(position.X, position.Y);
        int x = pending ? moveX : self.MoveX, y = pending ? moveY : self.MoveY;
        double ticks = Math.Min(MaxLead, now - (pending ? Math.Max(moveStarted, received) : received)) * FixedTickClock.TicksPerSecond;
        int distance = x != 0 && y != 0 ? 23 : 32;
        // Match the server's fixed X-then-Y sliding, including known living bodies.
        for (int i = 0; i < 4 && ticks > 0; i++, ticks -= 1)
        {
            int step = (int)Math.Round(distance * Math.Min(1, ticks));
            var horizontal = position with { X = position.X + x * step };
            if (CanOccupy(horizontal)) position = horizontal;
            var vertical = position with { Y = position.Y + y * step };
            if (CanOccupy(vertical)) position = vertical;
        }
        var predicted = new DisplayPosition(position.X, position.Y);
        double remaining = 1 - Math.Clamp((now - corrected) / CorrectionDuration, 0, 1);
        if (remaining == 0 || correction == default) return predicted;
        var adjusted = new DisplayPosition(predicted.X + correction.X * remaining, predicted.Y + correction.Y * remaining);
        return ClearCorrectionPath(predicted, adjusted) ? adjusted : predicted;
    }
    private bool ClearCorrectionPath(DisplayPosition from, DisplayPosition to)
    {
        // The offset is at most one body radius per axis. The four overlapping body
        // footprints cover the entire swept rectangle, including its interior.
        int left = (int)Math.Floor(Math.Min(from.X, to.X)), right = (int)Math.Ceiling(Math.Max(from.X, to.X));
        int top = (int)Math.Floor(Math.Min(from.Y, to.Y)), bottom = (int)Math.Ceiling(Math.Max(from.Y, to.Y));
        return CanOccupy(new(left, top)) && CanOccupy(new(right, top)) &&
            CanOccupy(new(left, bottom)) && CanOccupy(new(right, bottom));
    }
    private bool CanOccupy(GamePosition position) => grid.CanOccupy(position) && !state!.Entities.Any(e =>
        e.Id != self.Id && e.IsAlive && e.Region == self.Region &&
        Math.Abs((long)e.Position.X - position.X) < CollisionGrid.BodyRadius * 2 &&
        Math.Abs((long)e.Position.Y - position.Y) < CollisionGrid.BodyRadius * 2);
    private static void CheckTime(double now)
    {
        if (!double.IsFinite(now) || now < 0) throw new ArgumentOutOfRangeException(nameof(now));
    }
}
