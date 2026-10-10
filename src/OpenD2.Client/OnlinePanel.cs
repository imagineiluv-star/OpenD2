using Godot;
using OpenD2.Core;
using OpenD2.Online;
using System.Net.Http;

namespace OpenD2.Client;

public partial class OnlinePanel : VBoxContainer
{
    private OnlineClient? client;
    private string caFile = "";
    private readonly CancellationTokenSource lifetime = new();
    private readonly LineEdit address = new() { Text = "http://127.0.0.1:5080", PlaceholderText = "Server HTTPS address" };
    private readonly LineEdit username = new() { PlaceholderText = "Account (3–24 letters/digits)" };
    private readonly LineEdit password = new() { Secret = true, PlaceholderText = "Password (12–128 characters)" };
    private readonly LineEdit characterName = new() { PlaceholderText = "Character name" };
    private readonly LineEdit roomName = new() { Text = "Camp", PlaceholderText = "Room name" };
    private readonly LineEdit roomPassword = new() { Secret = true, PlaceholderText = "Optional room password (12+ characters)" };
    private readonly OptionButton characters = new(), rooms = new();
    private readonly OptionButton serverMode = new();
    private string SelectedMode => serverMode.Selected == 1 ? "open" : "realm";
    private readonly Label status = new() { Text = "Sign in to your OpenD2 server.", AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private readonly Label details = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private readonly OnlineArena arena = new() { CustomMinimumSize = new(640, 430), SizeFlagsVertical = SizeFlags.ExpandFill, FocusMode = FocusModeEnum.All };
    private CharacterInfo[] characterList = [];
    private RoomInfo[] roomList = [];
    private RoomView? state;
    private CancellationTokenSource? roomStream;
    private Exception? streamError;
    private long streamFrames;
    private readonly Queue<Func<Task>> requests = new();
    private bool busy, signedIn;
    private double poll;
    private int moveX, moveY;
    public override void _Ready()
    {
        AddChild(new Label { Text = "Online — account → character → room → cooperative arena", AutowrapMode = TextServer.AutowrapMode.WordSmart });
        serverMode.AddItem("Realm — server characters");
        serverMode.AddItem("Open — personal profiles (preview)");
        AddChild(serverMode);
        AddChild(address);
        var connection = new HFlowContainer(); AddChild(connection);
        var trust = new Label { Text = "Trust: system certificates", AutowrapMode = TextServer.AutowrapMode.WordSmart }; AddChild(trust);
        var certificate = new FileDialog { FileMode = FileDialog.FileModeEnum.OpenFile, Access = FileDialog.AccessEnum.Filesystem, Filters = ["*.pem,*.crt ; Public root CA certificate"] }; AddChild(certificate);
        certificate.FileSelected += file => { caFile = file; trust.Text = "Private CA selected: " + Path.GetFileName(file) + ". Check server to compare its SHA-256 with your administrator."; };
        Button(connection, "Check server", async () =>
        {
            using var probe = new OnlineClient(address.Text, caFile, SelectedMode);
            await probe.CheckServer(lifetime.Token);
            status.Text = "Server reachable; protocol and rules match. " + (probe.TrustedRootSha256 is { } hash ? "Private CA SHA-256: " + hash : "System certificate trust.");
        });
        Button(connection, "Choose private CA", () => { certificate.PopupCenteredRatio(0.7f); return Task.CompletedTask; });
        Button(connection, "Use system trust", () => { caFile = ""; trust.Text = "Trust: system certificates"; return Task.CompletedTask; });
        AddChild(username); AddChild(password);
        var auth = new HFlowContainer(); AddChild(auth);
        Button(auth, "Register", () => Login(true)); Button(auth, "Login", () => Login(false));
        Button(auth, "Logout", async () => { RequireLogin(); await client!.Send<object>(HttpMethod.Post, "v1/logout", cancellation: lifetime.Token); Reset(); });
        var chars = new HFlowContainer(); AddChild(chars); chars.AddChild(characterName); chars.AddChild(characters);
        Button(chars, "Create character", async () => { RequireLogin(); await client!.Send<CharacterInfo>(HttpMethod.Post, "v1/characters", new NameRequest(characterName.Text), lifetime.Token); await Refresh(); });
        Confirm(chars, "Delete selected", "Delete this character permanently?", async () => { RequireLogin(); await client!.Send<object>(HttpMethod.Delete, "v1/characters/" + SelectedCharacter(), cancellation: lifetime.Token); await Refresh(); });
        AddProfileControls();
        var lobby = new HFlowContainer(); AddChild(lobby); lobby.AddChild(roomName); lobby.AddChild(roomPassword); lobby.AddChild(rooms);
        Button(lobby, "Refresh", Refresh);
        Button(lobby, "Create room", async () => { RequireLogin(); Apply(await client!.Send<RoomView>(HttpMethod.Post, "v1/rooms", new RoomRequest(SelectedCharacter(), roomName.Text, roomPassword.Text), lifetime.Token)); });
        Button(lobby, "Join / reconnect", async () => { RequireLogin(); if (rooms.Selected < 0 || rooms.Selected >= roomList.Length) throw new InvalidOperationException("Select a room."); Apply(await client!.Send<RoomView>(HttpMethod.Post, $"v1/rooms/{roomList[rooms.Selected].Id}/join", new JoinRequest(SelectedCharacter(), roomPassword.Text), lifetime.Token)); });
        var actions = new HFlowContainer(); AddChild(actions);
        Button(actions, "Start (host)", async () => Apply(await Room<RoomView>("start")));
        Button(actions, "Save server progress", async () => { await Room<object>("save"); status.Text = "Server checkpoint saved."; });
        Button(actions, "Leave lobby", async () => { await Room<object>("leave"); ClearRoom(); await Refresh(); });
        Confirm(actions, "Close room (host)", "Close this room for everyone? Arena items are room-local; only victories carry to new rooms.", async () => { await Room<object>("close"); ClearRoom(); await Refresh(); });
        AddChild(status); AddChild(details); AddChild(arena);
        AddChild(new Label { Text = "Click arena to focus. WASD/arrows: move; Space: attack; Q: skill; F: pick up. Server judges all actions.\nFour-player preview arena. Original campaign/art is not connected to online play yet.", AutowrapMode = TextServer.AutowrapMode.WordSmart });
        if (OS.GetCmdlineUserArgs().Contains("--smoke-test")) GD.Print("OPEND2_ONLINE_UI_READY");
    }
    private void Button(Node parent, string text, Func<Task> action)
    {
        var button = new Godot.Button { Text = text }; parent.AddChild(button); button.Pressed += () => UserAction(action);
    }
    private void Confirm(Node parent, string text, string message, Func<Task> action)
    {
        var dialog = new ConfirmationDialog { DialogText = message }; AddChild(dialog);
        dialog.Confirmed += () => UserAction(action);
        var button = new Godot.Button { Text = text }; parent.AddChild(button);
        button.Pressed += () => dialog.PopupCentered();
    }
    private void UserAction(Func<Task> action)
    {
        var connection = client; var room = state?.Id;
        Guid? character = characters.Selected >= 0 && characters.Selected < characterList.Length ? characterList[characters.Selected].Id : null;
        _ = Run(async () =>
        {
            Guid? current = characters.Selected >= 0 && characters.Selected < characterList.Length ? characterList[characters.Selected].Id : null;
            if (client != connection || state?.Id != room || character != current) throw new InvalidOperationException("Selection changed; retry the action.");
            await action();
        }, true);
    }
    private async Task Run(Func<Task> action, bool queue = false)
    {
        if (busy)
        {
            if (queue && requests.Count < 8) requests.Enqueue(action);
            else if (queue) status.Text = "Please wait for pending requests.";
            return;
        }
        busy = true;
        try
        {
            while (true)
            {
                try { await action(); }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                catch (Exception e) { if (!lifetime.IsCancellationRequested) { status.Text = e.Message; if (e is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized }) Reset(); else if (e is HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound }) ClearRoom(); } }
                if (lifetime.IsCancellationRequested || !requests.TryDequeue(out var next)) break;
                action = next;
            }
        }
        finally { busy = false; }
    }
    private void RequireLogin() { if (!signedIn || client is null) throw new InvalidOperationException("Login first."); }
    private Guid SelectedCharacter() => characters.Selected >= 0 && characters.Selected < characterList.Length ? characterList[characters.Selected].Id : throw new InvalidOperationException("Select a character.");
    private async Task Login(bool register)
    {
        var next = new OnlineClient(address.Text, caFile, SelectedMode); var credentials = new Credentials(username.Text, password.Text); password.Text = "";
        try { await next.Login(credentials, register, lifetime.Token); }
        catch { next.Dispose(); throw; }
        requests.Clear(); StopRoomStream(); client?.Dispose(); client = next; signedIn = true; serverMode.Disabled = true; ClearRoom(); status.Text = "Logged in to " + client.Mode + ". Select a character and room."; await Refresh();
    }
    private async Task Refresh()
    {
        RequireLogin(); var selected = characters.Selected >= 0 && characters.Selected < characterList.Length ? characterList[characters.Selected].Id : Guid.Empty;
        characterList = await client!.Send<CharacterInfo[]>(HttpMethod.Get, "v1/characters", cancellation: lifetime.Token);
        roomList = await client.Send<RoomInfo[]>(HttpMethod.Get, "v1/rooms", cancellation: lifetime.Token);
        characters.Clear(); foreach (var c in characterList) characters.AddItem($"{c.Name} ({c.Victories} victories)");
        int index = Array.FindIndex(characterList, c => c.Id == selected); if (index >= 0) characters.Select(index);
        rooms.Clear(); foreach (var r in roomList) rooms.AddItem($"{r.Name} {r.Players}/4 {(r.Started ? "playing" : "lobby")} {(r.Locked ? "locked" : "")}");
    }
    private Task<T> Room<T>(string action)
    {
        RequireLogin(); if (state is null) throw new InvalidOperationException("Join a room first.");
        return client!.Send<T>(HttpMethod.Post, $"v1/rooms/{state.Id}/{action}", cancellation: lifetime.Token);
    }
    private void Apply(RoomView next)
    {
        bool changedRoom = state?.Id != next.Id;
        // HTTP command replies and pushed states use independent connections.
        // Never roll an input cursor or a running simulation back to an older view.
        if (!changedRoom && state is { } previous && (next.Tick < previous.Tick || next.NextSequence < previous.NextSequence || (previous.Started && !next.Started))) return;
        state = next; arena.State = next; arena.QueueRedraw();
        details.Text = $"{next.Name} | {(next.Started ? "Playing" : "Lobby")} | tick {next.Tick} | actor {next.Actor} | {client?.RoomTransport}\n" + string.Join(" / ", next.Members.Select(m => m.Name));
        if (next.Started) { var me = next.Entities.First(e => e.Id.Value == next.Actor); details.Text += $" | HP {me.Health}/{me.MaxHealth} | Mana {me.Mana}/{me.MaxMana}"; }
        if (changedRoom) StartRoomStream(next.Id);
    }
    private void StartRoomStream(Guid id)
    {
        StopRoomStream();
        var source = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        roomStream = source;
        _ = ReadRoomStream(client!, id, source);
    }
    private async Task ReadRoomStream(OnlineClient owner, Guid id, CancellationTokenSource source)
    {
        try
        {
            await foreach (var update in owner.WatchRoom(id, source.Token))
            {
                if (source.IsCancellationRequested || roomStream != source || client != owner || state?.Id != id) return;
                Apply(update); streamFrames++;
            }
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!source.IsCancellationRequested && roomStream == source && client == owner) streamError = error;
        }
        finally { source.Dispose(); }
    }
    private void StopRoomStream()
    {
        var previous = roomStream; roomStream = null;
        if (previous is not null)
        {
            try { previous.Cancel(); } catch (ObjectDisposedException) { }
        }
        streamError = null; streamFrames = 0;
    }
    private async Task WaitForRoomUpdate()
    {
        long before = streamFrames;
        for (int i = 0; i < 500; i++)
        {
            if (streamError is { } error) throw error;
            if (streamFrames > before) return;
            await Task.Delay(20, lifetime.Token);
        }
        throw new TimeoutException("Room stream did not deliver a fresh state.");
    }
    private void ClearRoom() { StopRoomStream(); state = null; arena.State = null; arena.QueueRedraw(); details.Text = ""; moveX = moveY = 0; }
    private void Reset() { requests.Clear(); signedIn = false; serverMode.Disabled = false; client?.Dispose(); client = null; ClearRoom(); characters.Clear(); rooms.Clear(); }
    public override void _Process(double delta)
    {
        if (state is null || client is null || busy) return;
        if (streamError is { } error)
        {
            ClearRoom();
            if (error is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized }) Reset();
            status.Text = "Room stream ended: " + error.Message + " Join/reconnect, or log in again after a server restart.";
            return;
        }
        poll += delta; if (poll < 0.1) return; poll = 0;
        bool focus = IsVisibleInTree() && arena.HasFocus() && GetWindow().HasFocus();
        int x = focus ? (Input.IsPhysicalKeyPressed(Key.D) || Input.IsPhysicalKeyPressed(Key.Right) ? 1 : 0) - (Input.IsPhysicalKeyPressed(Key.A) || Input.IsPhysicalKeyPressed(Key.Left) ? 1 : 0) : 0;
        int y = focus ? (Input.IsPhysicalKeyPressed(Key.S) || Input.IsPhysicalKeyPressed(Key.Down) ? 1 : 0) - (Input.IsPhysicalKeyPressed(Key.W) || Input.IsPhysicalKeyPressed(Key.Up) ? 1 : 0) : 0;
        _ = Run(async () =>
        {
            if (state!.Started && state.Entities.First(e => e.Id.Value == state.Actor).IsAlive && (x != 0 || y != 0 || x != moveX || y != moveY))
            { await InputCommand(CommandKind.SetMove, x, y); moveX = x; moveY = y; }
        });
    }
    private async Task InputCommand(CommandKind kind, int x = 0, int y = 0, uint target = 0, ulong item = 0)
    {
        if (state is null) return;
        Apply(await client!.Send<RoomView>(HttpMethod.Post, $"v1/rooms/{state.Id}/input", new InputRequest(state.NextSequence, kind, x, y, target, item), lifetime.Token));
    }
    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (!IsVisibleInTree() || !arena.HasFocus() || state is not { Started: true } || @event is not InputEventKey { Pressed: true, Echo: false } key) return;
        if (key.Keycode is Key.Space or Key.Q)
        {
            var me = state.Entities.First(e => e.Id.Value == state.Actor);
            var target = state.Entities.Where(e => e.Kind == EntityKind.Monster && e.IsAlive).OrderBy(e => Math.Abs(e.Position.X - me.Position.X) + Math.Abs(e.Position.Y - me.Position.Y)).FirstOrDefault();
            if (target.Id.Value != 0) UserAction(() => InputCommand(key.Keycode == Key.Q ? CommandKind.CastSkill : CommandKind.Attack, target: target.Id.Value));
        }
        else if (key.Keycode == Key.F)
        {
            var me = state.Entities.First(e => e.Id.Value == state.Actor);
            var item = state.Items.Where(i => i.Location == ItemLocation.Ground).OrderBy(i => Math.Abs(i.Position.X - me.Position.X) + Math.Abs(i.Position.Y - me.Position.Y)).FirstOrDefault();
            if (item.Id.Value != 0) UserAction(() => InputCommand(CommandKind.Pickup, item: item.Id.Value));
        }
        else return;
        GetViewport().SetInputAsHandled();
    }
    public override void _ExitTree() { StopRoomStream(); lifetime.Cancel(); client?.Dispose(); lifetime.Dispose(); }
}

public partial class OnlineArena : Control
{
    public RoomView? State { get; set; }
    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { Pressed: true }) GrabFocus();
        if (@event is InputEventKey key && key.PhysicalKeycode is Key.W or Key.A or Key.S or Key.D or Key.Up or Key.Down or Key.Left or Key.Right) AcceptEvent();
    }
    public override void _Draw()
    {
        float scale = Math.Min(Size.X / Arena.Width, Size.Y / Arena.Height); var grid = Arena.Grid();
        for (int y = 0; y < Arena.Height; y++) for (int x = 0; x < Arena.Width; x++) DrawRect(new(x * scale, y * scale, scale - 1, scale - 1), grid.At(x, y) == CollisionCell.Open ? new Color(0.12f, 0.19f, 0.14f) : new Color(0.28f, 0.28f, 0.3f));
        if (State is not { } state) return;
        foreach (var item in state.Items.Where(i => i.Location == ItemLocation.Ground)) DrawCircle(new Vector2(item.Position.X, item.Position.Y) / 256 * scale, scale * 0.13f, Colors.Gold);
        foreach (var e in state.Entities)
        {
            var p = new Vector2(e.Position.X, e.Position.Y) / 256 * scale;
            DrawCircle(p, scale * 0.28f, !e.IsAlive ? Colors.Gray : e.Kind == EntityKind.Monster ? Colors.IndianRed : e.Id.Value == state.Actor ? Colors.Cyan : Colors.CornflowerBlue);
            DrawString(ThemeDB.FallbackFont, p + new Vector2(-10, -12), $"{e.Id.Value}:{e.Health}", fontSize: 13);
        }
    }
}
