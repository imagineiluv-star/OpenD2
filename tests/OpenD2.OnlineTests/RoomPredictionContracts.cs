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
        Check(prediction.Position(applied.Entities[0], 1.08) == Exact(applied.Entities[0]), "authoritative correction on arrival");
        Check(prediction.Position(applied.Entities[0], 1.10).X == 816, "continue authoritative movement between frames");
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
        Console.WriteLine($"ROOM PREDICTION PASS movement-parity={comparisons} pending/receipt/WSS-order/stop/cancel/silence/reset/collision/immutability");
    }
}
