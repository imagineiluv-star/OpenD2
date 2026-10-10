using Godot;
using OpenD2.Core;
using OpenD2.Online;
using System.Net.Http;
using System.Text.Json;

namespace OpenD2.Client;

public partial class OnlinePanel
{
    // File barriers coordinate test processes only. The production server has no test endpoints.
    // Every gameplay action still uses the client's real HTTP transport and authoritative Core.
    private async Task CheckGameplay(string role, string secret, Guid character, string evidence)
    {
        status.Text = "Automated two-client gameplay validation. Manual input: NOT_RUN.";
        string peer = role == "host" ? "guest" : "host";
        Guid room = state!.Id;
        string path = $"v1/rooms/{room}";
        var checks = new List<string>();
        Directory.CreateDirectory(evidence);
        async Task Poll() => Apply(await client!.Send<RoomView>(HttpMethod.Get, path, cancellation: lifetime.Token));
        EntityState Entity(uint id) => state!.Entities.Single(e => e.Id.Value == id);
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidDataException(message);
            checks.Add(message); GD.Print($"ONLINE GAMEPLAY {role}: {message}");
        }
        void Signal(string stage) => File.WriteAllText(System.IO.Path.Combine(evidence, role + "-" + stage), "ready");
        async Task Wait(string name)
        {
            for (int i = 0; i < 300; i++)
            {
                if (File.Exists(System.IO.Path.Combine(evidence, name))) return;
                await Task.Delay(100, lifetime.Token);
            }
            throw new TimeoutException("Online gameplay barrier: " + name);
        }
        async Task Barrier(string stage) { Signal(stage); await Wait(peer + "-" + stage); }
        async Task Until(Func<bool> done, string failure, Func<Task>? action = null)
        {
            for (int i = 0; i < 120; i++)
            {
                await Poll();
                if (done()) return;
                File.AppendAllText(System.IO.Path.Combine(evidence, role + "-actions.jsonl"),
                    JsonSerializer.Serialize(new { step = failure, state!.Tick, state.Actor, state.Entities, state.Items }) + "\n");
                if (!Entity(state!.Actor).IsAlive) throw new InvalidDataException("Player died: " + failure);
                if (action is not null) await action();
                await Task.Delay(100, lifetime.Token);
            }
            throw new TimeoutException(failure);
        }
        async Task Approach(Func<GamePosition> position, int range = 250)
        {
            await Until(() => Distance(Entity(state!.Actor).Position, position()) <= range, "Approach target", async () =>
            {
                await MoveToward(position());
            });
            await InputCommand(CommandKind.SetMove);
        }
        async Task MoveToward(GamePosition target)
        {
            var move = GameplayDirection(state!.Entities, state.Actor, target);
            await InputCommand(CommandKind.SetMove, move.X, move.Y);
        }
        async Task Strike(CommandKind kind)
        {
            // The monster can change target after either player's approach. Follow the
            // current authoritative position instead of assuming it remains in range.
            var me = Entity(state!.Actor).Position; var target = Entity(100).Position;
            if (Distance(me, target) > GameSimulation.AttackRange - 32)
                await MoveToward(target);
            else
            {
                await InputCommand(CommandKind.SetMove);
                await InputCommand(kind, target: 100);
            }
        }
        async Task Capture(string stage)
        {
            await Poll();
            var view = new { state!.Id, state.Tick, state.Actor, state.StateHash, state.Members, state.Entities, state.Items };
            File.WriteAllText(System.IO.Path.Combine(evidence, $"{role}-{stage}.json"), JsonSerializer.Serialize(view));
            if (DisplayServer.GetName() != "headless")
            {
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var image = GetViewport().GetTexture().GetImage();
                if (image.IsEmpty() || image.SavePng(System.IO.Path.Combine(evidence, $"{role}-{stage}.png")) != Error.Ok)
                    throw new InvalidDataException("Rendered screenshot failed.");
            }
        }
        Check(CheckBodyBlocking(), "Recorded body-blocking regression reaches attack range");
        await Barrier("ready");
        await Capture("room");
        await Approach(() => Entity(100).Position, GameSimulation.AttackRange - 32);
        await Barrier("in-range");
        if (role == "host")
            await Until(() => Entity(100).Health < 60, "Host attack caused no damage", () => Strike(CommandKind.Attack));
        await Barrier("host-hit"); await Poll();
        Check(Entity(100).Health is > 0 and < 60, "Host damage visible in both clients");
        await Barrier("host-observed");
        if (role == "guest")
            await Until(() => Entity(state!.Actor).Mana < Entity(state.Actor).MaxMana, "Guest skill did not consume mana", () => Strike(CommandKind.CastSkill));
        await Barrier("guest-skill"); await Poll();
        Check(Entity(2).Mana < Entity(2).MaxMana && Entity(100).Health < 46, "Guest skill and mana visible in both clients");
        await Barrier("skill-observed");
        if (role == "host")
            await Until(() => !Entity(100).IsAlive, "Monster did not die", () => Strike(CommandKind.Attack));
        await Barrier("kill"); await Poll();
        Check(!Entity(100).IsAlive && state!.Items.Any(i => i.Location == ItemLocation.Ground), "Monster death and loot replicated");
        var loot = state!.Items.First(i => i.Location == ItemLocation.Ground);
        await Capture("combat");
        await Barrier("loot-observed");
        if (role == "host")
        {
            await Approach(() => loot.Position);
            await Until(() => state!.Items.Any(i => i.Id == loot.Id && i.Location == ItemLocation.Inventory), "Loot pickup failed", () => InputCommand(CommandKind.Pickup, item: loot.Id.Value));
        }
        await Barrier("pickup"); await Poll();
        Check(state!.Items.Single(i => i.Id == loot.Id) is { Location: ItemLocation.Inventory, Owner.Value: 1 }, "Exactly one player owns loot");
        if (role == "guest") await InputCommand(CommandKind.Pickup, item: loot.Id.Value);
        await Barrier("steal"); await Task.Delay(150, lifetime.Token); await Poll();
        Check(state!.Items.Single(i => i.Id == loot.Id).Owner.Value == 1, "Peer cannot steal inventory item");
        await Capture("loot");
        await Barrier("disconnect-ready");
        if (role == "host")
        {
            await InputCommand(CommandKind.SetMove, -1, 0);
            client!.Dispose(); client = null; Signal("disconnected");
            await Wait("guest-lease-checked");
            password.Text = secret; await Login(false);
            Apply(await client!.Send<RoomView>(HttpMethod.Post, path + "/join", new JoinRequest(character, ""), lifetime.Token));
            Check(state.Actor == 1 && state.Items.Single(i => i.Id == loot.Id).Owner.Value == 1, "Reconnect preserves actor and inventory");
        }
        else
        {
            await Wait("host-disconnected"); await Task.Delay(750, lifetime.Token); await Poll();
            var stopped = Entity(1).Position;
            await Task.Delay(350, lifetime.Token); await Poll();
            Check(Entity(1).Position == stopped, "Disconnected peer stops after input lease");
            Signal("lease-checked");
        }
        await Barrier("reconnected"); await Poll();
        Check(state!.Members.Length == 2 && state.Items.Single(i => i.Id == loot.Id).Owner.Value == 1, "Both clients retain same room after reconnect");
        await Capture("reconnect");
        await InputCommand(CommandKind.SetMove);
        await Barrier("stopped"); await Task.Delay(150, lifetime.Token); await Poll();
        var before = state!.Entities.ToDictionary(e => e.Id, e => (e.Position, e.Health));
        var beforeItems = state.Items.ToArray();
        if (role == "host") await Room<object>("save");
        await Barrier("saved");
        Signal("restart-ready");
        await Wait("server-restarted");
        password.Text = secret; await Login(false);
        Apply(await client!.Send<RoomView>(HttpMethod.Post, path + "/join", new JoinRequest(character, ""), lifetime.Token));
        Check(state!.Entities.All(e => before[e.Id] == (e.Position, e.Health)) && state.Items.SequenceEqual(beforeItems), "Hard server restart restores positions health and inventory");
        await Capture("restored"); await Barrier("restored");
        if (role == "host") await Room<object>("close");
        await Barrier("closed");
        try { await Poll(); throw new InvalidDataException("Closed room is still accessible."); }
        catch (HttpRequestException error) when (error.StatusCode == System.Net.HttpStatusCode.NotFound) { Check(true, "Room closure reaches both clients"); }
        File.WriteAllText(System.IO.Path.Combine(evidence, role + "-result.json"), JsonSerializer.Serialize(new
        {
            role, result = "PASS", checks, user_data = OS.GetUserDataDir(), rendering = DisplayServer.GetName() == "headless" ? "NOT_RUN" : "PASS",
            manual_gui = "NOT_RUN", multi_pc = "NOT_RUN"
        }, new JsonSerializerOptions { WriteIndented = true }));
        GD.Print("OPEND2_ONLINE_GAMEPLAY_PASS " + role + " MANUAL_GUI_NOT_RUN");
    }
    private static (int X, int Y) GameplayDirection(EntityState[] entities, uint actor, GamePosition target)
    {
        var me = entities.Single(e => e.Id.Value == actor);
        var directions = new (int X, int Y)[] { (1, 0), (0, 1), (-1, 0), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1) };
        var grid = Arena.Grid();
        // Three-tick lookahead avoids live bodies using the same collision bounds.
        var candidates = directions.Select(d => (Direction: d, Position: new GamePosition(me.Position.X + d.X * 96, me.Position.Y + d.Y * 96)))
            .Where(c => grid.CanOccupy(c.Position) && !entities.Any(e => e.IsAlive && e.Id != me.Id &&
                Math.Abs(e.Position.X - c.Position.X) < CollisionGrid.BodyRadius * 2 && Math.Abs(e.Position.Y - c.Position.Y) < CollisionGrid.BodyRadius * 2))
            .OrderBy(c => Distance(c.Position, target)).ToArray();
        if (candidates.Length == 0) throw new InvalidDataException("Test movement has no collision-free direction.");
        return candidates[0].Direction;
    }
    private static bool CheckBodyBlocking()
    {
        // Exact geometry from failed CI 38053719315: guest blocks the host's straight
        // approach. Exercise real Core movement/collision; no positions are mutated.
        return Scenario(new(990, 1044), new(1136, 1050)) && Scenario(new(894, 1044), new(1040, 1050));
        static bool Scenario(GamePosition host, GamePosition guest)
        {
        var game = new GameSimulation(42, [new(new(1), new(1), host),
            new(new(2), new(1), guest),
            new(new(100), new(1), new(1324, 1044), Kind: EntityKind.Monster, Health: 60, MaxHealth: 60)], Arena.Grid());
        for (int i = 0; i < 10; i++)
        {
            var target = game.GetEntity(new(100)).Position;
            if (Distance(game.GetEntity(new(1)).Position, target) <= GameSimulation.AttackRange - 32)
            {
                game.Submit(new(game.Tick + 1, 100, new(1), new(1), CommandKind.SetMove));
                game.Submit(new(game.Tick + 1, 101, new(1), new(1), CommandKind.Attack, Target: new(100)));
                game.Step();
                return game.GetEntity(new(2)).IsAlive && game.GetEntity(new(100)).Health < 60;
            }
            var direction = GameplayDirection(game.CaptureSnapshot().Entities.ToArray(), 1, target);
            if (game.Submit(new(game.Tick + 1, (uint)(i + 1), new(1), new(1), CommandKind.SetMove, direction.X, direction.Y)) != CommandResult.Accepted)
                return false;
            for (int tick = 0; tick < 3; tick++) game.Step();
        }
        return false;
        }
    }
    private static double Distance(GamePosition a, GamePosition b) => Math.Sqrt((long)(a.X - b.X) * (a.X - b.X) + (long)(a.Y - b.Y) * (a.Y - b.Y));
}
