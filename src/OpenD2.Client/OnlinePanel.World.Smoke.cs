using Godot;
using OpenD2.Core;
using OpenD2.Online;
using System.Net.Http;
using System.Text.Json;

namespace OpenD2.Client;

public partial class OnlinePanel
{
    private async Task CheckWorldView()
    {
        var game = CooperativeWorld.Create(2);
        var entities = game.Entities.ToArray();
        entities[1] = entities[1] with { Region = new(2), Position = new(768, 768) };
        var view = new RoomView(Guid.NewGuid(), "WorldView", true, false, 2, 1, 1, "authoritative",
            [new(Guid.NewGuid(), "Host", 1), new(Guid.NewGuid(), "Guest", 2)], entities,
            [new(new(1), ItemDefinition.HealthPotion, ItemLocation.Ground, default, -1, new(1), new(768, 768)),
             new(new(2), ItemDefinition.ManaPotion, ItemLocation.Ground, default, -1, new(2), new(768, 768))],
            0, new(CooperativeWorld.Content, game.Quest, 1));
        state = view; arena.SetState(view, 1);
        if (arena.Grid.Region.Value != 2 || arena.Grid.At(12, 6) != CollisionCell.Open || arena.VisibleEntities.Any(e => e.Region.Value != 2) ||
            arena.VisibleEntities.Any(e => e.Id.Value == 1) || arena.VisibleGroundItems.Single().Id.Value != 2 || arena.NearbyInteraction().Value != 201)
            throw new Exception("World renderer did not use the recipient region/grid/items/portal.");
        arena.PredictMove(1, 1, 0, 1);
        if (arena.Display(entities[1], 1.02).X != 784) throw new Exception("World movement prediction used the Arena grid.");
        var returned = view with { Tick = 2, World = view.World! with { RegionEpoch = 2 } };
        arena.SetState(returned, 1.04);
        if (arena.Display(entities[1], 1.06).X != 768 || arena.PredictionStarts != 0 || arena.PresentationTransitions != 0)
            throw new Exception("Same-region new visit retained old prediction/interpolation.");
        arena.SetState(view with { Tick = 3 }, 1.08);
        if (arena.State != returned) throw new Exception("Regressed region epoch replaced the display.");
        try { arena.SetState(returned with { World = returned.World! with { Content = new("wrong", "wrong") } }, 1.08); throw new Exception("Mismatched content displayed."); }
        catch (InvalidDataException) { }
        if (arena.State != returned) throw new Exception("Rejected content changed live rendering.");
        var input = MakeInput(returned, 1, CommandKind.Interact, target: 201);
        if (input.Region != 2 || input.RegionEpoch != 2) throw new Exception("World input omitted the recipient visit.");
        bool executed = false; busy = true; UserAction(() => { executed = true; return Task.CompletedTask; });
        Apply(returned); busy = false; await Run(() => Task.CompletedTask);
        if (executed || requests.Count != 0) throw new Exception("Queued old-region user action survived a portal.");
        arena.PredictMove(1, 1, 0); StopRoomStream();
        if (arena.PredictionStarts != 0 || arena.PresentationTransitions != 0) throw new Exception("Stream reset retained motion history.");
        ClearRoom();
        GD.Print("OPEND2_WORLD_VIEW_PASS region/grid/items/interaction/prediction/epoch/queue/content/reset");
    }

    // Uses the same create/join, keyboard interaction, input and view methods as
    // the product. File barriers carry public test progress only, never credentials.
    private async Task CheckWorldGameplay(string role, string secret, Guid character, string run, string evidence)
    {
        string peer = role == "host" ? "guest" : "host";
        var checks = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidDataException("World gameplay: " + name);
            checks.Add(name); GD.Print($"WORLD GAMEPLAY {role}: {name}");
        }
        string FileName(string name) => Path.Combine(evidence, name);
        void Signal(string stage) => File.WriteAllText(FileName(role + "-world-" + stage), "ready");
        async Task Wait(string name)
        {
            for (int i = 0; i < 300; i++)
            {
                if (File.Exists(FileName(name))) return;
                await Task.Delay(100, lifetime.Token);
            }
            throw new TimeoutException("World barrier: " + name);
        }
        async Task Barrier(string stage) { Signal(stage); await Wait(peer + "-world-" + stage); }
        async Task Until(Func<bool> done, string name, Func<Task>? action = null)
        {
            for (int i = 0; i < 200; i++)
            {
                await WaitForRoomUpdate();
                if (done()) return;
                if (action is not null) await action();
                await Task.Delay(40, lifetime.Token);
            }
            throw new TimeoutException("World observation: " + name);
        }
        EntityState Me() => state!.Entities.Single(e => e.Id.Value == state.Actor);
        EntityState Actor(uint id) => state!.Entities.Single(e => e.Id.Value == id);
        async Task Stop()
        {
            long sequence = nextInputSequence;
            await InputCommand(CommandKind.SetMove);
            await Until(() => state!.ProcessedSequence >= sequence, "stop consumed");
        }
        async Task Approach(GamePosition point, int range = 140)
        {
            await Until(() => Distance(Me().Position, point) <= range, "approach " + point, async () =>
            {
                var move = GameplayDirection(state!.Entities, state.Actor, point, arena.Grid);
                await InputCommand(CommandKind.SetMove, move.X, move.Y);
            });
            await Stop();
        }
        async Task PressInteract(uint expected)
        {
            Check(arena.NearbyInteraction().Value == expected, "keyboard target " + expected);
            long sequence = nextInputSequence;
            arena.GrabFocus();
            using var key = new InputEventKey { Keycode = Key.E, PhysicalKeycode = Key.E, Pressed = true };
            _UnhandledKeyInput(key);
            // Smoke owns the busy flag, so the real keyboard callback queues its
            // action exactly like a user pressing E during another HTTP request.
            Check(requests.Count == 1, "E key queued one world interaction");
            busy = false; await Run(() => Task.CompletedTask); busy = true;
            Check(nextInputSequence == sequence + 2, "world interaction used movement/action batch");
            await Until(() => state!.ProcessedSequence >= sequence + 1, "interaction consumed");
        }
        async Task Capture(string stage)
        {
            await WaitForRoomUpdate();
            Check(arena.Grid.Region == Me().Region && arena.VisibleEntities.All(e => e.Region == Me().Region) &&
                arena.VisibleGroundItems.All(i => i.Region == Me().Region), "draw scope " + stage);
            if (DisplayServer.GetName() == "headless") return;
            await CaptureOnlineFrame(FileName(role + "-world-" + stage + ".png"));
        }
        Check(client!.SupportsWorldRooms, "v5 world capability");
        await Refresh(); characters.Select(Array.FindIndex(characterList, c => c.Id == character));
        Check(worldChoice.ItemCount == 2 && !worldChoice.Disabled, "matching world choice available");
        worldChoice.Select(1); roomName.Text = "world" + run; roomPassword.Text = "";
        if (role == "host") { await CreateRoom(); Signal("created"); }
        else
        {
            await Wait(peer + "-world-created"); await Refresh();
            rooms.Select(Array.FindIndex(roomList, r => r.Name == roomName.Text)); await JoinSelectedRoom();
        }
        await Until(() => state!.Started, "world started", async () =>
        {
            if (role == "host" && state!.Members.Length == 2 && !state.Started) Apply(await Room<RoomView>("start"));
        });
        Guid room = state!.Id;
        Check(state.World?.Content == CooperativeWorld.Content && state.Members.Length == 2 && worldChoice.Disabled && worldChoice.Selected == 1,
            "world create/join selection binding");
        await Barrier("started"); await Capture("camp");
        // Quests are personal. The host accepts first; the guest has not spoken to the Guide, so their own quest stays
        // available. They then walk to the Guide (clear of the host's body, inside interaction range) and accept for themselves.
        if (role == "host")
        {
            await PressInteract(300);
            await Until(() => state!.World!.Quest.Stage == QuestStage.Active, "own quest accepted");
        }
        await Barrier("host-accepted");
        if (role == "guest")
        {
            await WaitForRoomUpdate();
            Check(state!.World!.Quest.Stage == QuestStage.Available && details.Text.Contains("Quest: Available", StringComparison.Ordinal),
                "the host's acceptance leaves the guest's quest available");
            await Approach(new(512, 600), 48); await PressInteract(300);
            await Until(() => state!.World!.Quest.Stage == QuestStage.Active, "own quest accepted");
        }
        Check(details.Text.Contains("Quest: Active", StringComparison.Ordinal), "own quest HUD");
        await Barrier("quest");
        if (role == "host") { await Approach(new(1024, 768)); await PressInteract(200); }
        await Until(() => Actor(1).Region.Value == 2, "host portal");
        Check(Me().Region.Value == (role == "host" ? 2u : 1u) && arena.VisibleEntities.All(e => e.Id.Value != (role == "host" ? 2u : 1u)),
            "split regions hide remote party bodies");
        Check(details.Text.Contains("Camp", StringComparison.Ordinal) && details.Text.Contains("Field", StringComparison.Ordinal), "party region labels");
        await Barrier("split"); await Capture("split");
        if (role == "host") await Approach(new(512, 768), 48);
        await Barrier("arrival-clear");
        if (role == "guest") { await Approach(new(1024, 768)); await PressInteract(200); }
        await Until(() => Actor(2).Region.Value == 2, "guest portal");
        Check(arena.VisibleEntities.Count(e => e.Kind == EntityKind.Player) == 2, "same-region reunion renders both players");
        await Barrier("joined"); await Capture("joined");
        if (role == "host") { await Approach(new(1024, 768)); await PressInteract(201); }
        await Until(() => Actor(1).Region.Value == 1, "host return portal");
        await Barrier("returned");
        foreach (string disconnected in new[] { "host", "guest" })
        {
            uint actor = disconnected == "host" ? 1u : 2u;
            await Barrier(disconnected + "-disconnect-ready");
            if (role == disconnected)
            {
                var region = Me().Region; var world = state!.World;
                await InputCommand(CommandKind.SetMove, -1); long sequence = nextInputSequence;
                StopRoomStream(); client!.Dispose(); client = null; Signal(disconnected + "-disconnected");
                await Wait(peer + "-world-" + disconnected + "-lease-checked");
                password.Text = secret; await Login(false);
                characters.Select(Array.FindIndex(characterList, c => c.Id == character));
                rooms.Select(Array.FindIndex(roomList, r => r.Id == room)); await JoinSelectedRoom();
                await WaitForRoomUpdate();
                Check(state!.World == world && Me().Region == region && state.Actor == actor && nextInputSequence == sequence && arena.Grid.Region == region,
                    disconnected + " reconnect preserves content/region/quest/epoch/cursor");
                await InputCommand(CommandKind.SetMove, 0, 1); Signal(disconnected + "-input-sent");
                await Wait(peer + "-world-" + disconnected + "-input-observed"); await Stop();
            }
            else
            {
                await Wait(peer + "-world-" + disconnected + "-disconnected");
                await Task.Delay(750, lifetime.Token); await WaitForRoomUpdate();
                var stopped = Actor(actor).Position;
                await Task.Delay(350, lifetime.Token); await WaitForRoomUpdate();
                Check(Actor(actor).Position == stopped && Actor(actor).MoveX == 0 && Actor(actor).MoveY == 0, disconnected + " disconnected world lease stops");
                Signal(disconnected + "-lease-checked");
                await Wait(peer + "-world-" + disconnected + "-input-sent");
                await Until(() => Actor(actor).MoveY == 1, "resumed world movement"); Signal(disconnected + "-input-observed");
            }
            await Barrier(disconnected + "-reconnected");
        }
        await Stop(); await Barrier("stopped"); await WaitForRoomUpdate();
        var before = state!.Entities.Select(e => (e.Id, e.Region, e.Position, e.Health)).ToArray();
        var beforeWorld = state.World; var beforeItems = state.Items.ToArray();
        if (role == "host") await Room<object>("save");
        await Barrier("saved"); Signal("restart-ready"); await Wait("server-world-restarted");
        password.Text = secret; await Login(false);
        characters.Select(Array.FindIndex(characterList, c => c.Id == character));
        rooms.Select(Array.FindIndex(roomList, r => r.Id == room)); await JoinSelectedRoom(); await WaitForRoomUpdate();
        Check(state!.World == beforeWorld && state.Entities.Select(e => (e.Id, e.Region, e.Position, e.Health)).SequenceEqual(before) && state.Items.SequenceEqual(beforeItems),
            "hard restart restores world regions quest epochs and items");
        Check(arena.Grid.Region == Me().Region, "restored renderer selects own region");
        await Capture("restored"); await Barrier("restored");
        if (role == "host") await Room<object>("close");
        await Barrier("closed");
        try
        {
            async Task Closed() { while (true) await WaitForRoomUpdate(); }
            await Closed().WaitAsync(TimeSpan.FromSeconds(10), lifetime.Token);
            throw new InvalidDataException("Closed world remains accessible.");
        }
        catch (HttpRequestException error) when (error.StatusCode == System.Net.HttpStatusCode.NotFound) { Check(true, "world closure reaches both clients"); }
        ClearRoom();
        File.WriteAllText(FileName(role + "-world-result.json"), JsonSerializer.Serialize(new
        {
            role, mode = client!.Mode, result = "PASS", checks, room, character, content = CooperativeWorld.Content,
            rendering = DisplayServer.GetName() == "headless" ? "NOT_RUN" : "PASS", observations = "room-websocket-stream",
            http_state_polling = false, manual_gui = "NOT_RUN", physical_multi_pc = "NOT_RUN", original_mpq = "NOT_RUN"
        }, new JsonSerializerOptions { WriteIndented = true }));
        GD.Print("OPEND2_WORLD_GAMEPLAY_PASS " + role);
    }
}
