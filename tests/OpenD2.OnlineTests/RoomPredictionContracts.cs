using System.Text.Json;
using OpenD2.Core;
using OpenD2.Online;

internal static class RoomPredictionContracts
{
    private static void Check(bool yes, string reason) { if (!yes) throw new Exception("Room prediction: " + reason); }
    internal static void Run()
    {
        var grid = Arena.Grid(); var prediction = new RoomPrediction(grid);
        var entity = new EntityState(new(1), new(1), new(768, 768));
        RoomView first = new(Guid.NewGuid(), "Prediction", true, true, 1, 1, 0, "opaque", [], [entity], []);
        DisplayPosition Exact(EntityState e) => new(e.Position.X, e.Position.Y);
        void Start(RoomView state, int x = 1, int y = 0)
        {
            prediction.Reset(); prediction.Push(state, 1); prediction.Move(state.NextSequence, x, y, 1);
        }
        Start(first);
        Check(prediction.Position(entity, 1.02).X == 784, "movement before HTTP receipt");
        prediction.Accept(new(first.Id, 1, 2, 0));
        Check(prediction.Position(entity, 1.04).X == 800, "receipt acknowledges queueing only");
        var queued = first with { NextSequence = 2 };
        prediction.Push(queued, 1.06);
        Check(prediction.Position(entity, 1.08).X == 832, "same-tick acceptance does not rebase time");
        var applied = queued with { Tick = 1, Entities = [entity with { Position = new(800, 768), MoveX = 1 }] };
        prediction.Push(applied, 1.08);
        Check(prediction.Position(applied.Entities[0], 1.08).X == 832, "small correction preserves displayed position on arrival");
        Check(Math.Abs(prediction.Position(applied.Entities[0], 1.10).X - 840) < 0.000001, "continue movement while correction decays");
        prediction.Move(2, 0, 0, 1.10);
        Check(prediction.Position(applied.Entities[0], 1.12) == Exact(applied.Entities[0]), "stop does not extrapolate old direction");
        prediction.Cancel();
        Check(prediction.Position(applied.Entities[0], 1.14) == Exact(applied.Entities[0]), "failed or uncertain input cancels prediction");
        // A server snapshot can overtake the HTTP response, including a stopped/blocked move.
        Start(first);
        var stopped = queued with { Tick = 1 };
        prediction.Push(stopped, 1.04);
        prediction.Accept(new(first.Id, 1, 2, 0));
        Check(prediction.Position(entity, 1.06) == Exact(entity), "late receipt cannot replay already applied movement");
        Start(first); prediction.Push(queued, 1.04); prediction.Push(stopped, 1.08);
        Check(prediction.Position(entity, 1.10) == Exact(entity), "later tick retires observed queued cursor without HTTP");
        Start(first);
        prediction.Accept(new(Guid.NewGuid(), 1, 2, 0)); prediction.Accept(new(first.Id, 2, 2, 0)); prediction.Accept(new(first.Id, 1, 3, 0));
        Check(prediction.Position(entity, 1.04).X == 800, "foreign/unrelated receipt ignored");
        prediction.Push(first, 1.15);
        Check(prediction.Position(entity, 1.17) == Exact(entity) && !prediction.IsAnimating(1.17), "duplicate tick cannot extend silence horizon");
        Start(first); prediction.Push(first with { Tick = 1 }, 1.12);
        Check(prediction.Position(entity, 1.17) == Exact(entity), "unconfirmed request expires despite fresh snapshots");
        string unchanged = JsonSerializer.Serialize(first);
        // Compare each predicted fixed step with the authoritative movement solver, including
        // walls, corners, diagonal normalization, living body blockers and map limits.
        int comparisons = 0;
        foreach (var start in new[] { new GamePosition(768, 768), new(320, 320), new(3008, 1792), new(2816, 1472) })
        foreach (int x in new[] { -1, 0, 1 }) foreach (int y in new[] { -1, 0, 1 })
        foreach (bool body in new[] { false, true })
        {
            var me = entity with { Position = start };
            var blocker = new EntityState(new(2), new(1), new(start.X + 160, start.Y));
            if (body && !grid.CanOccupy(blocker.Position)) continue;
            EntityState[] actors = body ? [me, blocker] : [me];
            var game = new GameSimulation(42, actors, grid);
            var state = first with { Entities = actors };
            Start(state, x, y);
            Check(game.Submit(new(1, 1, me.Id, me.Region, CommandKind.SetMove, x, y)) == CommandResult.Accepted, "reference move accepted");
            for (int step = 1; step <= 3; step++)
            {
                game.Step(); var expected = game.Entities.ToArray().Single(e => e.Id == me.Id);
                Check(prediction.Position(me, 1 + step * 0.04) == Exact(expected), "server movement/collision parity"); comparisons++;
            }
            if (body) Check(prediction.Position(blocker, 1.04) == Exact(blocker), "remote entity never predicted");
            Check(actors[0] == me, "source entity unchanged");
        }
        void Snap(RoomView state, double now)
        {
            Start(first); prediction.Push(state, now);
            var me = state.Entities.Single(e => e.Id.Value == state.Actor);
            Check(prediction.Position(me, now + 0.02) == Exact(me), "discontinuity clears local intent");
        }
        Snap(first with { Id = Guid.NewGuid() }, 1.04);
        Snap(first with { Actor = 2, Entities = [entity with { Id = new(2) }] }, 1.04);
        Snap(first with { Started = false }, 1.04);
        Snap(first with { Tick = 10 }, 1.04);
        Snap(first, 1.3); Snap(first, 0.5);
        foreach (var changed in new[] { entity with { Region = new(2) }, entity with { Health = 0 }, entity with { Health = 99 },
            entity with { HitStun = 1 }, entity with { AttackCooldown = 1 }, entity with { Position = new(2048, 768) } })
            Snap(first with { Entities = [changed] }, 1.04);
        Start(first with { Tick = 2 }); prediction.Push(first, 1.04);
        Check(prediction.Position(entity, 1.04).X == 800, "stale tick ignored");
        prediction.Reset(); prediction.Push(applied, 2);
        Check(prediction.Position(applied.Entities[0], 2.02) == Exact(applied.Entities[0]), "reconnect does not inherit prediction");
        Check(JsonSerializer.Serialize(first) == unchanged, "snapshot/hash immutable");
        foreach (double time in new[] { double.NaN, double.PositiveInfinity, -1 })
        {
            try { prediction.Position(entity, time); throw new Exception("Invalid clock accepted."); } catch (ArgumentOutOfRangeException) { }
        }
        Corrections(first);
        Console.WriteLine($"ROOM PREDICTION PASS movement-parity={comparisons} pending/receipt/WSS-order/stop/cancel/silence/reset/collision/immutability");
    }
    private static void Corrections(RoomView first)
    {
        var grid = Arena.Grid(); var display = new RoomPrediction(grid);
        var me = first.Entities[0];
        var moving = first with { Tick = 1, NextSequence = 2, Entities = [me with { Position = new(784, 768), MoveX = 1 }] };
        bool Near(double a, double b) => Math.Abs(a - b) < 0.000001;
        void Start()
        {
            display.Reset(); display.Push(first, 1); display.Move(1, 1, 0, 1); display.Accept(new(first.Id, 1, 2, 0));
        }
        Start(); display.Push(moving, 1.04);
        Check(Near(display.Position(moving.Entities[0], 1.04).X, 800), "correction begins at previous display");
        Check(Near(display.Position(moving.Entities[0], 1.08).X, 824), "correction halves after 40ms");
        Check(Near(display.Position(moving.Entities[0], 1.12).X, 848), "correction ends after 80ms");
        display.Push(moving, 1.06);
        Check(Near(display.Position(moving.Entities[0], 1.12).X, 848), "duplicate tick cannot restart correction");
        Start(); display.Push(moving, 1.04);
        double before = display.Position(moving.Entities[0], 1.05).X;
        display.Move(2, 1, 0, 1.05);
        Check(Near(display.Position(moving.Entities[0], 1.05).X, before), "same-direction refresh preserves display");
        display.Move(3, 0, 0, 1.06);
        Check(display.Position(moving.Entities[0], 1.06).X == 784, "stop clears correction immediately");
        Start(); display.Push(moving, 1.04); display.Cancel();
        Check(display.Position(moving.Entities[0], 1.06).X == 784, "uncertain command clears correction");
        Start(); display.Push(moving, 1.04);
        var next = moving with { Tick = 2, Entities = [moving.Entities[0] with { Position = new(800, 768) }] };
        before = display.Position(moving.Entities[0], 1.06).X;
        display.Push(next, 1.06);
        Check(Near(display.Position(next.Entities[0], 1.06).X, before), "new tick starts from current corrected display");
        foreach (var changed in new[] { me with { Position = new(1000, 768), MoveX = 1 }, // Error exceeds cap.
            me with { Position = new(784, 768), MoveX = -1 }, // Direction reversal.
            me with { Position = new(784, 768), MoveX = 1, Health = 99 },
            me with { Position = new(784, 768), MoveX = 1, HitStun = 1 },
            me with { Position = new(784, 768), MoveX = 1, AttackCooldown = 1 } })
        {
            Start(); display.Push(moving with { Entities = [changed] }, 1.04);
            Check(display.Position(changed, 1.04).X == changed.Position.X, "discontinuity uses authoritative position immediately");
        }
        // A newly observed body blocks the old visual position even though the server position is valid.
        Start();
        var blocked = moving with { Entities = [moving.Entities[0], new(new(2), new(1), new(944, 768))] };
        display.Push(blocked, 1.08);
        Check(display.Position(blocked.Entities[0], 1.08).X == 784, "new body prevents correction overlap");
        // Correction must be rechecked as prediction approaches the body between updates.
        Start(); display.Push(blocked, 1.04);
        Check(display.Position(blocked.Entities[0], 1.08).X == 816, "blocked correction falls back to safe predicted coordinate");
        // A diagonal correction crossing the wall's corner cannot use two walkable endpoints.
        var corner = first with { Entities = [me with { Position = new(3008, 1472) }] };
        display.Reset(); display.Push(corner, 1); display.Move(1, 1, 0, 1);
        var around = corner with { Tick = 1, NextSequence = 2,
            Entities = [me with { Position = new(3008, 1536), MoveX = 1 }] };
        display.Push(around, 1.08);
        Check(display.Position(around.Entities[0], 1.08) == new DisplayPosition(3008, 1536), "swept rectangle prevents corner cutting");
        Start(); display.Push(moving, 1.04); display.Push(moving, 1.19);
        Check(display.Position(moving.Entities[0], 1.21).X == 784 && !display.IsAnimating(1.21), "silence cannot retain visual correction");
        Start(); display.Push(moving, 1.04); display.Reset(); display.Push(moving, 2);
        Check(display.Position(moving.Entities[0], 2.02).X == 784, "reconnect clears visual error");
        // Jittered updates never mutate authority; residual error stays capped and expires.
        display.Reset(); var state = first; double arrival = 3; int x = 768;
        display.Push(state, arrival);
        for (int i = 0; i < 600; i++)
        {
            arrival += i % 3 == 0 ? 0.02 : i % 3 == 1 ? 0.04 : 0.06;
            int direction = i % 40 < 20 ? 1 : -1; x += 32 * direction;
            state = state with { Tick = i + 1, Entities = [me with { Position = new(x, 768), MoveX = direction }] };
            string untouched = JsonSerializer.Serialize(state); display.Push(state, arrival);
            for (int sample = 0; sample <= 4; sample++)
            {
                double time = arrival + sample * 0.02;
                var p = display.Position(state.Entities[0], time);
                double baseline = x + direction * sample * 16;
                Check(Math.Abs(p.X - baseline) <= RoomPrediction.MaxCorrection + 0.000001, "jitter correction bounded");
                Check(grid.CanOccupy(new((int)Math.Floor(p.X), (int)Math.Floor(p.Y))), "jitter display remains walkable");
                if (sample == 4) Check(Near(p.X, baseline), "jitter correction fully expires");
            }
            Check(JsonSerializer.Serialize(state) == untouched, "corrected display cannot mutate authority");
        }
        Console.WriteLine("ROOM CORRECTION PASS jitter=600 continuity/80ms-decay/64-unit-cap/duplicates/refresh/stop/reset/bodies/corners/immutability");
    }

}
