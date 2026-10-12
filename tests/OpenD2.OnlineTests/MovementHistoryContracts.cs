using System.Text.Json;
using OpenD2.Core;
using OpenD2.Online;

internal static class MovementHistoryContracts
{
    private static void Check(bool yes, string why) { if (!yes) throw new Exception("Movement history: " + why); }
    internal static void Run()
    {
        var grid = Arena.Grid(); var display = new RoomPrediction(grid);
        var me = new EntityState(new(1), new(1), new(768, 768));
        var first = new RoomView(Guid.NewGuid(), "History", true, true, 1, 1, 0, "opaque", [], [me], [], 0);
        void Start() { display.Reset(); display.Push(first, 1); }
        Start(); display.Move(1, 1, 0, 1); display.Move(2, 0, 1, 1.04);
        display.Accept(new(Guid.NewGuid(), 1, 2, 10)); display.Accept(new(first.Id, 2, 2, 10)); display.Accept(new(first.Id, 1, 99, 10));
        Check(display.Position(me, 1.08) == new DisplayPosition(800, 800), "ordered turn retains first movement");
        var partial = first with { Tick = 1, NextSequence = 3, ProcessedSequence = 1,
            Entities = [me with { Position = new(800, 768), MoveX = 1 }] };
        display.Push(partial, 1.08);
        Check(display.PendingMoves == 1 && display.Position(partial.Entities[0], 1.08) == new DisplayPosition(800, 800), "partial acknowledgement replays suffix immediately");
        display.Accept(new(first.Id, 1, 2, 0));
        Check(display.PendingMoves == 1, "late consumed receipt cannot restore history");
        display.Move(3, 0, 0, 1.08);
        Check(display.Position(partial.Entities[0], 1.12) == new DisplayPosition(800, 800), "stop freezes replayed path");
        display.Push(partial, 1.10);
        Check(display.Position(partial.Entities[0], 1.12) == new DisplayPosition(800, 800), "duplicate snapshot cannot restart replay clock");
        display.Cancel(); Check(display.PendingMoves == 0 && display.Position(partial.Entities[0], 1.12).Y == 768, "uncertain action cancels entire history");
        Start(); display.Move(1, 1, 0, 1); display.Move(2, 0, 1, 1.02);
        Check(display.Position(me, 1.04) == new DisplayPosition(768, 800), "last move in a tick wins like Core");
        display.Move(2, -1, 0, 1.03); display.Move(1, -1, 0, 1.03);
        Check(display.PendingMoves == 2 && display.Position(me, 1.04) == new DisplayPosition(768, 800), "duplicate and reversed sequences ignored");
        Start(); display.Move(1, 1, 0, 1); display.Accept(new(first.Id, 1, 2, 1));
        Check(display.Position(me, 1.04).X == 768 && display.Position(me, 1.08).X == 800, "receipt replaces estimated execution tick");
        Start(); display.Move(1, 1, 0, 1); display.Move(2, 0, 1, 1.04);
        display.Accept(new(first.Id, 1, 3, 0)); display.Accept(new(first.Id, 1, 2, 0));
        Check(display.Position(me, 1.08) == new DisplayPosition(768, 832), "out-of-order receipts preserve same-tick sequence order");
        Start();
        for (int i = 1; i <= RoomPrediction.MaxPendingMoves; i++) display.Move(i, 1, 0, 1);
        Check(display.PendingMoves == RoomPrediction.MaxPendingMoves, "bounded history fills");
        display.Move(RoomPrediction.MaxPendingMoves + 1, 1, 0, 1);
        Check(display.PendingMoves == 0 && display.Position(me, 1.02).X == 768, "overflow cancels without partial replay");
        Start(); display.Move(1, 1, 0, 1); display.Move(2, 0, 1, 1.12);
        display.Push(first with { Tick = 1, NextSequence = 3 }, 1.14);
        Check(!display.IsAnimating(1.17), "oldest unconsumed input expires despite newer input and snapshot");
        display.Push(partial with { Tick = 2 }, 1.18);
        Check(display.PendingMoves == 1 && display.IsAnimating(1.18), "consumed expired prefix cannot discard fresh suffix");
        Start(); display.Move(1, 1, 0, 1); display.Move(2, 0, 1, 1.12);
        display.Push(first with { Tick = 1, NextSequence = 3 }, 1.14);
        display.Move(3, 1, 0, 1.17);
        Check(display.PendingMoves == 0, "expired history is cleared before fresh input");
        foreach (var changed in new[] { first with { Id = Guid.NewGuid() }, first with { Tick = 10 }, first with { ProcessedSequence = null },
            first with { Entities = [me with { Health = 99 }] }, first with { Entities = [me with { AttackCooldown = 1 }] } })
        {
            Start(); display.Move(1, 1, 0, 1); display.Push(changed, 1.04);
            Check(display.PendingMoves == 0, "discontinuity or capability loss clears history");
        }
        Start(); display.Move(1, 1, 0, 1.04); display.Move(2, 0, 1, 1.02);
        Check(display.PendingMoves == 0, "local clock rewind cancels history");
        Start(); display.Move(1, 1, 0, 1); display.Reset(); display.Push(first, 2);
        Check(display.PendingMoves == 0 && display.Position(me, 2.04).X == 768, "reconnect starts without history");
        // Independent Core simulation exercises every three-command direction combination,
        // including stops, diagonals, walls and bodies. The second snapshot consumes only
        // the first command while two commands remain locally predicted.
        int comparisons = 0;
        var directions = new[] { (0, 0), (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (-1, -1) };
        foreach (var origin in new[] { new GamePosition(768, 768), new(3008, 1472), new(320, 320) })
        foreach (bool body in new[] { false, true })
        foreach (var a in directions) foreach (var b in directions) foreach (var c in directions)
        {
            var actor = me with { Position = origin };
            EntityState[] actors = body ? [actor, new(new(2), new(1), new(origin.X + 160, origin.Y))] : [actor];
            if (body && !grid.CanOccupy(actors[1].Position)) continue;
            var game = new GameSimulation(42, actors, grid);
            var state = first with { Entities = actors }; string original = JsonSerializer.Serialize(state);
            display.Reset(); display.Push(state, 1);
            var moves = new[] { a, b, c }; RoomView? prefix = null;
            for (int i = 0; i < moves.Length; i++)
            {
                display.Move(i + 1, moves[i].Item1, moves[i].Item2, 1 + i * 0.04);
                Check(game.Submit(new(i + 1, (ulong)i + 1, actor.Id, actor.Region, CommandKind.SetMove, moves[i].Item1, moves[i].Item2)) == CommandResult.Accepted, "Core accepted reference input");
                game.Step();
                var expected = game.GetEntity(actor.Id).Position;
                Check(display.Position(actor, 1 + (i + 1) * 0.04) == new DisplayPosition(expected.X, expected.Y), "full history matches Core movement"); comparisons++;
                if (i == 0) prefix = state with { Tick = 1, NextSequence = 4, ProcessedSequence = 1, Entities = game.Entities.ToArray() };
            }
            display.Push(prefix!, 1.12);
            var final = game.GetEntity(actor.Id).Position;
            Check(display.PendingMoves == 2 && display.Position(prefix!.Entities[0], 1.12) == new DisplayPosition(final.X, final.Y), "replayed suffix matches Core after partial acknowledgement"); comparisons++;
            Check(JsonSerializer.Serialize(state) == original, "replay leaves authoritative arrays/hash untouched");
        }
        Console.WriteLine($"MOVEMENT HISTORY PASS core-comparisons={comparisons} turns/stops/partial-ack/receipt-order/collision/16-input-cap/160ms/reset/immutability");
    }
}
