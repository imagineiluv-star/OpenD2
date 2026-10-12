using System.Text.Json;
using OpenD2.Core;
using OpenD2.Online;

internal static class AdaptivePresentationContracts
{
    internal static void Run()
    {
        var display = new RoomPresentation();
        var entities = Arena.Create(2).Entities.ToArray();
        RoomView first = new(Guid.NewGuid(), "Adaptive", true, true, 1, 1, 0, "unchanged", [], entities, []);
        static void Check(bool value) { if (!value) throw new Exception("Adaptive presentation contract failed."); }
        static bool Near(double a, double b) => Math.Abs(a - b) < 0.000001;
        static RoomView Move(RoomView view, long tick, int x) => view with { Tick = tick,
            Entities = view.Entities.Select(e => e with { Position = new(x, e.Position.Y) }).ToArray() };
        display.Push(first, 1);
        var second = Move(first, 1, 800); display.Push(second, 1.12);
        Check(Near(display.Delay, RoomPresentation.MaxDuration));
        Check(Near(display.Position(second.Entities[1], 1.18).X, 784));
        Check(display.Position(second.Entities[0], 1.18).X == 800);
        // Duplicate metadata preserves both the estimator and this track's deadline.
        var metadata = second with { NextSequence = 2 };
        display.Push(metadata, 1.18);
        Check(Near(display.Delay, 0.12) && Near(display.Position(metadata.Entities[1], 1.18).X, 784));
        Check(Near(display.Position(metadata.Entities[1], 1.24).X, 800));
        // Estimator changes apply to new transitions, never stretch an existing one.
        display.Push(metadata with { Tick = 2 }, 1.20);
        Check(Near(display.Position(metadata.Entities[1], 1.24).X, 800));
        // Metadata must not become the clock anchor for the next advancing tick.
        display.Reset(); display.Push(first, 2);
        display.Push(first, 2.03); display.Push(second, 2.04);
        Check(Near(display.Delay, 0.04));
        display.Push(second, 2.10); display.Push(second, 2.19);
        var third = Move(first, 2, 832); display.Push(third, 2.25);
        Check(Near(display.Delay, 0.04) && display.Position(third.Entities[1], 2.25).X == 832);
        // Tick gaps use server cadence; batching at the same timestamp reaches the cap.
        display.Reset(); display.Push(first, 3);
        display.Push(second with { Tick = 3 }, 3.12); Check(Near(display.Delay, 0.04));
        display.Push(third with { Tick = 6 }, 3.12); Check(Near(display.Delay, 0.12));
        double delay = display.Delay;
        Check(!display.Push(first, 3.13) && display.Delay == delay);
        // Invalid arrays must not commit a new estimate or tick anchor.
        try { display.Push(third with { Tick = 7, Entities = [entities[0], entities[0]] }, 3.16);
            throw new Exception("Duplicate actor accepted."); } catch (ArgumentException) { }
        Check(display.Delay == delay);
        // Stable delivery slowly returns to the baseline without falling below it.
        double now = 3.12; var state = third with { Tick = 6 };
        for (int i = 0; i < 256; i++)
        {
            now += 0.04; state = Move(state, state.Tick + 1, 768 + i % 5 * 32);
            display.Push(state, now);
            Check(display.Delay <= delay && display.Delay >= RoomPresentation.Duration);
            delay = display.Delay;
        }
        Check(Near(display.Delay, 0.04));
        // Reset, new room/actor, tick jump, stopped session and clock rewind discard history.
        foreach (var changed in new[] { second with { Id = Guid.NewGuid() }, second with { Actor = 2 },
            second with { Tick = 9 }, second with { Started = false } })
        {
            display.Reset(); display.Push(first, 4); display.Push(second, 4.12);
            display.Push(changed, 4.16); Check(Near(display.Delay, 0.04));
        }
        display.Reset(); display.Push(first, 4); display.Push(second, 4.12);
        display.Push(third, 3); Check(Near(display.Delay, 0.04));
        display.Reset(); Check(Near(display.Delay, 0.04));
        // Deterministic long jitter/burst sequence: continuity, bounded delay and positions,
        // immutable authority, local immediacy, finite completion after transport silence.
        display.Push(first, 5); state = first; now = 5;
        for (int i = 0; i < 1200; i++)
        {
            now += (i % 4) switch { 0 => 0.0, 1 => 0.02, 2 => 0.04, _ => 0.10 };
            double from = display.Position(state.Entities[1], now).X;
            int x = 768 + i % 7 * 32;
            state = Move(state, state.Tick + 1, x);
            string original = JsonSerializer.Serialize(state);
            display.Push(state, now);
            Check(Near(display.Position(state.Entities[1], now).X, from));
            Check(display.Delay >= 0.04 && display.Delay <= 0.12);
            for (int j = 0; j <= 12; j++)
            {
                double position = display.Position(state.Entities[1], now + j * 0.01).X;
                Check(position >= Math.Min(from, x) - 0.000001 && position <= Math.Max(from, x) + 0.000001);
            }
            Check(display.Position(state.Entities[0], now).X == x);
            Check(Near(display.Position(state.Entities[1], now + 0.121).X, x));
            Check(!display.IsAnimating(now + 0.121) && JsonSerializer.Serialize(state) == original);
        }
        Console.WriteLine("ADAPTIVE PRESENTATION PASS jitter=1200 delay=40-120ms duplicates/cadence/bursts/decay/reset/immutability");
    }
}
