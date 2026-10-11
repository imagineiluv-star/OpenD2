using System.Text.Json;
using OpenD2.Core;
using OpenD2.Online;

internal static class RoomPresentationContracts
{
    internal static void Run()
    {
        var display = new RoomPresentation();
        var entities = Arena.Create(2).Entities.ToArray();
        RoomView state = new(Guid.NewGuid(), "Presentation", true, true, 1, 1, 10, "hash", [], entities, []);
        static void Check(bool yes) { if (!yes) throw new Exception("Room presentation contract failed."); }
        static bool Near(double a, double b) => Math.Abs(a - b) < 0.000001;
        RoomView Move(RoomView view, int x, long tick) => view with { Tick = tick, Entities = view.Entities.Select(e => e.Id.Value is 1 or 2 ? e with { Position = new(x, e.Position.Y) } : e).ToArray() };
        void Exact(RoomView view, double now)
        {
            foreach (var e in view.Entities) Check(display.Position(e, now) == new DisplayPosition(e.Position.X, e.Position.Y));
        }
        Check(display.Push(state, 1)); Exact(state, 1); Check(!display.IsAnimating(1));
        var next = Move(state, 800, 11);
        string serialized = JsonSerializer.Serialize(next);
        Check(display.Push(next, 1.04));
        Check(Near(display.Position(next.Entities[1], 1.04).X, 768));
        Check(Near(display.Position(next.Entities[1], 1.06).X, 784));
        Check(display.Position(next.Entities[0], 1.06).X == 800); // Local actor is never delayed.
        Exact(next, 1.08); Exact(next, 9); Check(!display.IsAnimating(9));
        // Duplicate ticks must not restart the transition; non-position data stays authoritative.
        var health = next with { Entities = next.Entities.Select(e => e.Id.Value == 2 ? e with { Health = 99 } : e).ToArray() };
        Check(display.Push(health, 1.06));
        Check(Near(display.Position(health.Entities[1], 1.06).X, 784));
        Check(Near(display.Position(health.Entities[1], 1.08).X, 800));
        Check(!display.Push(state, 1.07));
        Check(Near(display.Position(health.Entities[1], 1.08).X, 800));
        Check(JsonSerializer.Serialize(next) == serialized && entities[1].Position.X == 768);
        // An early next update starts at the currently displayed coordinate, not an old endpoint.
        display.Reset(); display.Push(state, 1); display.Push(next, 1.04);
        var early = Move(next, 832, 12); display.Push(early, 1.06);
        Check(Near(display.Position(early.Entities[1], 1.06).X, 784));
        Check(Near(display.Position(early.Entities[1], 1.08).X, 808));
        Exact(early, 1.11);
        display.Reset(); display.Push(state, 1);
        var reordered = next with { Entities = next.Entities.Reverse().ToArray() };
        display.Push(reordered, 1.04);
        Check(Near(display.Position(reordered.Entities.Single(e => e.Id.Value == 2), 1.06).X, 784));
        Check(display.Position(reordered.Entities.Single(e => e.Id.Value == 1), 1.06).X == 800);
        void Snap(RoomView changed, double now)
        {
            display.Reset(); display.Push(state, 1); Check(display.Push(changed, now)); Exact(changed, now);
        }
        Snap(next with { Id = Guid.NewGuid() }, 1.04);
        Snap(next with { Actor = 2 }, 1.04);
        Snap(next with { Started = false }, 1.04);
        Snap(next with { Tick = 14 }, 1.04); // Large tick gap.
        Snap(next, 1.3); // Network pause.
        Snap(next, 0.5); // A reset caller clock cannot interpolate across clock epochs.
        Snap(Move(state, 800, 10), 1.04); // Same-tick coordinate replacement.
        Snap(Move(state, 2048, 11), 1.04); // Teleport.
        foreach (var changed in new[] { next.Entities[1] with { Region = new(2) },
            next.Entities[1] with { Health = 0 }, next.Entities[1] with { Kind = EntityKind.Monster } })
            Snap(next with { Entities = [next.Entities[0], changed] }, 1.04);
        display.Reset(); display.Push(state with { Entities = state.Entities.Select(e => e.Id.Value == 2 ? e with { Health = 0 } : e).ToArray() }, 1);
        display.Push(next, 1.04); Exact(next, 1.04); // Resurrection.
        display.Push(next with { Entities = [next.Entities[0]], Tick = 12 }, 1.08);
        Check(display.Count == 1);
        display.Push(next with { Tick = 13 }, 1.12); Exact(next, 1.12); // Reintroduced ID has no retained track.
        display.Reset(); Check(display.Count == 0 && display.Transitions == 0 && !display.IsAnimating(2));
        display.Push(next, 2); Exact(next, 2);
        foreach (double time in new[] { double.NaN, double.PositiveInfinity, -1 })
        {
            try { display.Push(next, time); throw new Exception("Invalid clock accepted."); } catch (ArgumentOutOfRangeException) { }
        }
        foreach (var invalid in new[] { next with { Entities = [next.Entities[0], next.Entities[0]] },
            next with { Entities = Enumerable.Repeat(next.Entities[0], GameSimulation.MaxEntities + 1).ToArray() } })
        {
            try { display.Push(invalid, 2.01); throw new Exception("Invalid entities accepted."); } catch (ArgumentException) { }
            Check(display.Count == next.Entities.Length); Exact(next, 2.01);
        }
        // Long deterministic jitter sequence: bounded positions, no extrapolation or snapshot mutation.
        display.Reset(); display.Push(state, 3); double arrival = 3; int x = 768;
        for (int i = 0; i < 600; i++)
        {
            arrival += i % 3 == 0 ? 0.02 : i % 3 == 1 ? 0.04 : 0.06;
            double before = display.Position(state.Entities[1], arrival).X;
            x += i % 40 < 20 ? 32 : -32;
            var frame = Move(state, x, state.Tick + 1); string untouched = JsonSerializer.Serialize(frame);
            Check(display.Push(frame, arrival));
            Check(Near(display.Position(frame.Entities[1], arrival).X, before));
            for (int sample = 0; sample <= 4; sample++)
            {
                var p = display.Position(frame.Entities[1], arrival + sample * 0.01);
                Check(p.X >= Math.Min(before, x) - 0.000001 && p.X <= Math.Max(before, x) + 0.000001);
            }
            Exact(frame, arrival + 1); Check(JsonSerializer.Serialize(frame) == untouched);
            state = frame;
        }
        Check(display.Count == entities.Length && display.Transitions > 0);
        Console.WriteLine("ROOM PRESENTATION PASS 600 jittered states/local-immediacy/no-extrapolation/immutability/duplicates/stale/reset/discontinuities/budgets");
    }
}
