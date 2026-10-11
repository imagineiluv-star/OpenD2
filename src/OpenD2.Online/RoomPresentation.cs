using OpenD2.Core;

namespace OpenD2.Online;

public readonly record struct DisplayPosition(double X, double Y);

// Render-only positions. Never modifies snapshots, simulation, input or state hashes.
// The caller owns this on its UI thread and supplies monotonic seconds.
public sealed class RoomPresentation
{
    public const double Duration = 1.0 / FixedTickClock.TicksPerSecond, MaxSilence = 0.2;
    private readonly record struct Track(EntityState Entity, DisplayPosition From, double Started);
    private Dictionary<EntityId, Track> current = new(), spare = new();
    private Guid room;
    private uint actor;
    private long tick;
    private bool started, hasState;
    private double received, animationUntil;
    public long Transitions { get; private set; }
    public int Count => current.Count;

    public void Reset()
    {
        current.Clear(); spare.Clear(); hasState = false; animationUntil = 0; Transitions = 0;
    }
    public bool Push(RoomView state, double now)
    {
        CheckTime(now);
        ArgumentNullException.ThrowIfNull(state);
        if (state.Entities is null || state.Entities.Length > GameSimulation.MaxEntities) throw new ArgumentException("Entity budget exceeded.", nameof(state));
        bool sameSession = hasState && room == state.Id && actor == state.Actor && started == state.Started;
        if (sameSession && state.Tick < tick) return false;
        bool continuous = sameSession && started && now >= received && now - received <= MaxSilence && state.Tick - tick <= 3;
        spare.Clear(); double until = 0; long transitions = 0;
        foreach (var entity in state.Entities)
        {
            var position = new DisplayPosition(entity.Position.X, entity.Position.Y);
            var track = new Track(entity, position, now);
            if (continuous && entity.Id.Value != state.Actor && current.TryGetValue(entity.Id, out var old) &&
                entity.Region == old.Entity.Region && entity.Kind == old.Entity.Kind && entity.IsAlive && old.Entity.IsAlive &&
                Math.Abs((long)entity.Position.X - old.Entity.Position.X) <= GameSimulation.UnitsPerTile &&
                Math.Abs((long)entity.Position.Y - old.Entity.Position.Y) <= GameSimulation.UnitsPerTile)
            {
                if (entity.Position == old.Entity.Position) track = old with { Entity = entity };
                else if (state.Tick > tick)
                {
                    track = new(entity, Sample(old, now), now); transitions++;
                }
            }
            if (!spare.TryAdd(entity.Id, track)) throw new ArgumentException("Duplicate entity.", nameof(state));
            if (track.From != position) until = Math.Max(until, track.Started + Duration);
        }
        (current, spare) = (spare, current);
        room = state.Id; actor = state.Actor; tick = state.Tick; started = state.Started; received = now; hasState = true;
        animationUntil = until; Transitions += transitions;
        return true;
    }
    public DisplayPosition Position(EntityState entity, double now)
    {
        CheckTime(now);
        return current.TryGetValue(entity.Id, out var track) && track.Entity == entity
            ? Sample(track, now) : new(entity.Position.X, entity.Position.Y);
    }
    public bool IsAnimating(double now) { CheckTime(now); return hasState && now < animationUntil; }
    private static DisplayPosition Sample(Track track, double now)
    {
        double fraction = Math.Clamp((now - track.Started) / Duration, 0, 1);
        return new(track.From.X + (track.Entity.Position.X - track.From.X) * fraction,
            track.From.Y + (track.Entity.Position.Y - track.From.Y) * fraction);
    }
    private static void CheckTime(double now)
    {
        if (!double.IsFinite(now) || now < 0) throw new ArgumentOutOfRangeException(nameof(now));
    }
}
