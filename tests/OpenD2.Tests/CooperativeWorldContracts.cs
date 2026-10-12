using OpenD2.Core;

internal static class CooperativeWorldContracts
{
    private static readonly RegionId Town = new(1), Dungeon = new(2), Remote = new(3);
    private static readonly EntityState One = new(new(1), Town, new(384, 384), Health: 50);
    private static readonly EntityState Two = new(new(2), Dungeon, new(4480, 384), Health: 50);
    private static readonly EntityState Monster = new(new(100), Dungeon, new(4736, 384), Kind: EntityKind.Monster, Health: 1);
    private static readonly WorldNpc Npc = new(new(1000), Town, new(384, 640), "Guide");
    private static readonly WorldPortal Entrance = new(new(1001), Town, new(640, 384), Dungeon, new(4480, 384));
    private static readonly WorldPortal Exit = new(new(1002), Dungeon, new(4480, 640), Town, new(384, 384));
    private static void Check(bool value) { if (!value) throw new Exception("Cooperative world contract failed."); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}."); }
    private static CollisionGrid Grid(RegionId region, bool wall = false)
    {
        var cells = Enumerable.Repeat(CollisionCell.Open, 144).ToArray();
        if (wall) cells[14] = CollisionCell.Blocked;
        return new(region, 12, 12, cells, new(((int)region.Value - 1) * 4096, 0));
    }
    private static WorldDefinition World(bool townWall = false) => new(
        [new("Town", Grid(Town, townWall)), new("Dungeon", Grid(Dungeon)), new("Remote", Grid(Remote))],
        [townWall ? Entrance with { Position = new(640, 640) } : Entrance, Exit], Npc, [Monster.Id], "Shared cellar", playerLimit: 4);
    private static GameSimulation Game(EntityState[]? actors = null, WorldDefinition? world = null) =>
        new(1, actors ?? [One, Two, Monster], world: world ?? World());
    private static GameCommand Command(GameSimulation game, uint actor, CommandKind kind, uint target = 0, int x = 0, ItemId item = default) =>
        new(game.Tick + 1, game.CaptureSnapshot().Inputs.Single(c => c.Actor.Value == actor).Sequence + 1,
            new(actor), game.GetEntity(new(actor)).Region, kind, X: x, Target: new(target), Item: item);
    private static void Accept(GameSimulation game, GameCommand command) => Check(game.Submit(command) == CommandResult.Accepted);

    public static void Run(string root, Action<string, Action> test)
    {
        test("Cooperative worlds require explicit content identity and preserve incompatible save files", () =>
        {
            var world = World();
            var single = new WorldDefinition(world.Regions.ToArray(), world.Portals.ToArray(), Npc, [Monster.Id], world.QuestTitle);
            Check(single.PlayerLimit == 1 && world.ContentHash != single.ContentHash);
            Throws<ArgumentException>(() => Game(world: single));
            foreach (int invalid in new[] { 0, 5 })
                Throws<ArgumentOutOfRangeException>(() => new WorldDefinition(world.Regions.ToArray(), [], Npc, [Monster.Id], "Quest", playerLimit: invalid));
            string file = Path.Combine(root, "cooperative-content.json"); GameSave.Save(file, Game().CaptureSnapshot());
            byte[] original = File.ReadAllBytes(file);
            Throws<SaveCompatibilityException>(() => GameSave.Load(file, world: single));
            Check(File.ReadAllBytes(file).SequenceEqual(original));
        });
        test("Cooperative world uses each actor's terrain and advances every occupied region", () =>
        {
            var game = Game([One with { Position = new(448, 384) }, Two with { Position = new(4544, 384) }, Monster with { Health = 0 }], World(true));
            Accept(game, Command(game, 1, CommandKind.SetMove, x: 1)); Accept(game, Command(game, 2, CommandKind.SetMove, x: 1)); game.Step();
            Check(game.GetEntity(new(1)).Position.X == 448 && game.GetEntity(new(2)).Position.X == 4576);
            Check(game.WorldPlayerCount == 2 && game.WorldPlayer.Value == 1 && game.ActiveRegion == Town && game.Collision!.Region == Town);
            Check(game.IsRegionActive(Town) && game.IsRegionActive(Dungeon) && !game.IsRegionActive(Remote));
        });
        test("Cooperative monsters attack in separate regions while empty-region timers remain frozen", () =>
        {
            var townMonster = Monster with { Id = new(101), Region = Town, Position = new(640, 384), Health = 100 };
            var frozen = Monster with { Id = new(102), Region = Remote, Position = new(8576, 384), Health = 100, HitStun = 2, MoveX = 1 };
            var game = Game([One, Two, Monster with { Health = 100 }, townMonster, frozen]); game.Step();
            Check(game.GetEntity(new(1)).Health < 50 && game.GetEntity(new(2)).Health < 50);
            Check(game.GetEntity(frozen.Id) == frozen && game.Events.ToArray().Count(e => e.Kind == SimulationEventKind.Hit) == 2);
        });
        test("Cooperative combat loot can be picked up using the remote region grid", () =>
        {
            var game = Game(); Accept(game, Command(game, 2, CommandKind.Attack, 100)); game.Step();
            Check(!game.GetEntity(Monster.Id).IsAlive);
            var item = game.Items[0]; Accept(game, Command(game, 2, CommandKind.Pickup, item: item.Id)); game.Step();
            Check(game.Items[0].Owner.Value == 2 && game.Items[0].Location == ItemLocation.Inventory);
        });
        test("Simultaneous portal entry reserves one arrival and cancels only the winner's inputs", () =>
        {
            var game = Game([One, Two with { Region = Town, Position = new(384, 640) }, Monster with { Health = 0 }]);
            Accept(game, Command(game, 1, CommandKind.Interact, 1001)); Accept(game, Command(game, 2, CommandKind.Interact, 1001));
            Accept(game, new(10, 2, new(1), Town, CommandKind.Signal)); Accept(game, new(10, 2, new(2), Town, CommandKind.Signal));
            game.Step();
            Check(game.GetEntity(new(1)).Region == Dungeon && game.GetEntity(new(2)).Region == Town && game.PendingCommands == 1);
            Check(game.Events.ToArray().Single(e => e.Kind == SimulationEventKind.RegionChanged).Value == 1);
            Check(game.Events.ToArray().Single(e => e.Kind == SimulationEventKind.InteractionFailed).Value == (int)InteractionFailure.BlockedArrival);
            Check(game.Submit(new(2, 2, new(1), Dungeon, CommandKind.Signal)) == CommandResult.StaleSequence);
            Accept(game, new(2, 3, new(1), Dungeon, CommandKind.Signal));
            for (int i = 0; i < 9; i++) game.Step();
            Check(game.Events.ToArray().Single(e => e.Kind == SimulationEventKind.Signaled).Actor.Value == 2);
        });
        test("A secondary player transfers independently of the legacy view and retains terrain collision", () =>
        {
            var game = Game([One with { Position = new(1408, 1408) }, Two with { Region = Town, Position = new(384, 640) }, Monster with { Health = 0 }]);
            Accept(game, Command(game, 2, CommandKind.Interact, 1001)); game.Step();
            Check(game.ActiveRegion == Town && game.Collision!.Region == Town && game.GetEntity(new(2)).Region == Dungeon);
            Accept(game, Command(game, 2, CommandKind.SetMove, x: 1)); game.Step();
            Check(game.GetEntity(new(2)).Position.X == 4512);
            var restored = GameSimulation.Restore(game.CaptureSnapshot()); Check(restored.ComputeStateHash() == game.ComputeStateHash());
        });
        test("Shared quest dialogue advances once per tick and rewards living party members only once", () =>
        {
            var game = Game([One, Two with { Region = Town, Position = new(384, 640) },
                new(new(3), Remote, new(8576, 384), Health: 30), new(new(4), Remote, new(8832, 384), Health: 0), Monster with { Health = 0 }]);
            for (uint actor = 1; actor <= 2; actor++) Accept(game, Command(game, actor, CommandKind.Interact, 1000));
            game.Step(); Check(game.QuestState == QuestStage.ReadyToTurnIn && game.GetEntity(new(1)).Health == 50);
            for (uint actor = 1; actor <= 2; actor++) Accept(game, Command(game, actor, CommandKind.Interact, 1000));
            game.Step(); Check(game.QuestState == QuestStage.Completed && game.GetEntity(new(1)).Health == 100 && game.GetEntity(new(2)).Health == 100);
            Check(game.GetEntity(new(3)).Health == 100 && game.GetEntity(new(4)).Health == 0);
            Check(game.Events.ToArray().Count(e => e.Kind == SimulationEventKind.QuestChanged) == 1);
            var checkpoint = game.CaptureSnapshot();
            game = GameSimulation.Restore(checkpoint with { Entities = checkpoint.Entities.Select(e => e.Id.Value == 3 ? e with { Health = 20 } : e).ToArray() });
            Accept(game, Command(game, 2, CommandKind.Interact, 1000)); game.Step(); Check(game.GetEntity(new(3)).Health == 20);
        });
        test("Different players retain their final interaction within the same tick", () =>
        {
            var game = Game([One, Two with { Region = Town, Position = new(384, 640) }, Monster with { Health = 0 }]);
            Accept(game, Command(game, 1, CommandKind.Interact, 1000)); Accept(game, Command(game, 1, CommandKind.Interact, 1001));
            Accept(game, Command(game, 2, CommandKind.Interact, 1000)); game.Step();
            Check(game.GetEntity(new(1)).Region == Dungeon && game.GetEntity(new(2)).Region == Town && game.QuestState == QuestStage.ReadyToTurnIn);
            Check(game.Events.ToArray().Count(e => e.Kind == SimulationEventKind.NpcTalked) == 1);
        });
        test("Portal queue filtering preserves peer tick budgets and scheduling cursors", () =>
        {
            var game = Game([One, Two with { Region = Town, Position = new(1408, 1408) }, Monster with { Health = 0 }]);
            Accept(game, Command(game, 1, CommandKind.Interact, 1001));
            for (ulong i = 0; i < 64; i++) Accept(game, new(2, i + 2, new(1), Town, CommandKind.Signal));
            for (ulong i = 0; i < 64; i++) Accept(game, new(2, i + 1, new(2), Town, CommandKind.Signal));
            game.Step(); Check(game.PendingCommands == 64);
            for (ulong i = 65; i <= 128; i++) Accept(game, new(2, i, new(2), Town, CommandKind.Signal));
            Check(game.Submit(new(2, 129, new(2), Town, CommandKind.Signal)) == CommandResult.TickFull);
            game.Step(); Check(game.Events.Length == 128 && game.Events.ToArray().All(e => e.Actor.Value == 2));
        });
        test("Cooperative checkpoint and disk save restore world identity, queues, quest and owned copies", () =>
        {
            var game = Game(); Accept(game, Command(game, 1, CommandKind.Interact, 1000)); game.Step();
            Accept(game, new(10, 1, new(2), Dungeon, CommandKind.Attack, Target: Monster.Id));
            var snapshot = game.CaptureSnapshot(); string hash = game.ComputeStateHash();
            string file = Path.Combine(root, "cooperative-world.json"); GameSave.Save(file, snapshot);
            var loaded = GameSave.Load(file, world: game.World).Simulation;
            Check(loaded.WorldPlayerCount == 2 && loaded.ComputeStateHash() == hash);
            for (int i = 0; i < 30; i++) { game.Step(); loaded.Step(); Check(game.ComputeStateHash() == loaded.ComputeStateHash()); }
            Check(snapshot.QuestState == QuestStage.Active && snapshot.Entities[2].IsAlive && snapshot.PendingCommands.Count == 1);
            Throws<InvalidDataException>(() => GameSimulation.Restore(snapshot with { WorldPlayer = new(2) }));
        });
        test("Cooperative world replay is deterministic across actor submission order and checkpoint continuation", () =>
        {
            EntityState[] actors = [One, Two with { Region = Town, Position = new(384, 640) },
                new(new(3), Remote, new(8576, 384)), new(new(4), Remote, new(8832, 384)), Monster with { Health = 0 }];
            var a = Game(actors); var b = Game(actors.Reverse().ToArray()); var trace = new List<RecordedCommand>();
            for (int tick = 1; tick <= 300; tick++)
            {
                var batch = Enumerable.Range(1, 4).Select(id => Command(a, (uint)id,
                    tick == 1 && id <= 2 ? CommandKind.Interact : CommandKind.Signal, tick == 1 && id <= 2 ? 1001u : 0u)).ToArray();
                foreach (var command in batch) { Accept(a, command); trace.Add(new(tick - 1, command)); }
                foreach (var command in batch.Reverse()) Accept(b, command);
                a.Step(); b.Step(); Check(a.Events.SequenceEqual(b.Events) && a.ComputeStateHash() == b.ComputeStateHash());
                if (tick == 150) b = GameSimulation.Restore(b.CaptureSnapshot());
            }
            Check(GameSimulation.Replay(1, actors, trace, 300, world: a.World).ComputeStateHash() == a.ComputeStateHash());
        });
        test("Cooperative worlds enforce four players and reserve every regional combat slot", () =>
        {
            var players = Enumerable.Range(1, 5).Select(i => One with { Id = new((uint)i), Position = new(384, 128 + i * 256) });
            Throws<ArgumentException>(() => Game(players.Append(Monster).ToArray()));
            var residents = Enumerable.Range(100, 125).Select(i => Monster with { Id = new((uint)i), Health = 0 });
            Throws<ArgumentException>(() => Game(players.Take(4).Concat(residents).ToArray()));
            Check(Game(players.Take(4).Concat(residents.Take(124)).ToArray()).WorldPlayerCount == 4);
        });
        test("Cooperative region stepping and warmed portal queue filtering allocate no tick memory", () =>
        {
            var game = Game([One, Two with { Region = Town, Position = new(1408, 1408) }, Monster with { Health = 0 }]);
            // Warm both transfer directions and the existing priority queue capacity.
            for (int i = 0; i < 20; i++)
            {
                Accept(game, Command(game, 1, CommandKind.Interact, i % 2 == 0 ? 1001u : 1002u));
                Accept(game, new(game.Tick + 2, (ulong)i + 1, new(2), Town, CommandKind.Signal)); game.Step();
            }
            var portal = Command(game, 1, CommandKind.Interact, 1001);
            Accept(game, portal); long allocated = GC.GetAllocatedBytesForCurrentThread(); game.Step();
            Check(GC.GetAllocatedBytesForCurrentThread() == allocated && game.GetEntity(new(1)).Region == Dungeon);
            for (int i = 0; i < 20; i++) game.Step();
            allocated = GC.GetAllocatedBytesForCurrentThread(); for (int i = 0; i < 1000; i++) game.Step();
            Check(GC.GetAllocatedBytesForCurrentThread() == allocated);
        });
    }
}
