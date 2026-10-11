using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenD2.Core;
using OpenD2.Online;

namespace OpenD2.Server;

// One process, one owner lock. Simulation never trusts client actor, tick or outcomes.
public sealed partial class Realm : IDisposable
{
    // Arrays are owned by this snapshot and read only by the internal stream serializer.
    private sealed record RoomSnapshot(GameSimulation Source, long Tick, string Hash, EntityState[] Entities, ItemState[] Items);
    private sealed class Room(SavedRoom saved, int rules = GameSimulation.RulesVersion)
    {
        public byte[]? InviteHash;
        public DateTimeOffset InviteUntil;
        public SavedRoom Saved = saved;
        public GameSimulation? Game = saved.Game?.Restore(rules);
        public RoomSnapshot? Snapshot;
        public readonly Dictionary<Guid, DateTimeOffset> Seen = new();
        public readonly Dictionary<Guid, DateTimeOffset> Moved = new();
    }
    private readonly object gate = new();
    private readonly string file;
    private readonly FileStream writerLock;
    private readonly TimeProvider clock;
    private readonly List<Account> accounts;
    private readonly List<Character> characters;
    private readonly Dictionary<Guid, Room> rooms;
    private readonly Dictionary<string, (Guid Account, DateTimeOffset Until)> sessions = new();
    private bool failed;
    private long snapshotBuilds;
    internal long SnapshotBuilds { get { lock (gate) return snapshotBuilds; } }
    public string Mode { get; }
    public Realm(string directory, TimeProvider? time = null, string mode = "realm")
    {
        if (mode is not ("realm" or "open")) throw new ArgumentException("Unknown online mode.", nameof(mode));
        Mode = mode;
        clock = time ?? TimeProvider.System;
        Directory.CreateDirectory(directory);
        file = Path.Combine(directory, "realm.json");
        writerLock = new(Path.Combine(directory, "realm.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            if (File.Exists(file) && new FileInfo(file).Length > 16 * 1024 * 1024) throw new InvalidDataException("Realm exceeds storage budget.");
            var db = File.Exists(file) ? JsonSerializer.Deserialize<Database>(File.ReadAllBytes(file)) ?? throw new InvalidDataException("Empty realm.") : new(2, GameSimulation.RulesVersion, [], [], [], Mode);
            if (db.Version is not (1 or 2) || db.RulesVersion is not (7 or GameSimulation.RulesVersion) || db.Accounts.Length > 256 || db.Characters.Length > 1024 || db.Rooms.Length > 32) throw new InvalidDataException("Unsupported realm version/budget.");
            string? storedMode = db.Version == 1 && db.Mode is null ? "realm" : db.Mode;
            if (storedMode != Mode || (db.Version == 1 && Mode != "realm")) throw new InvalidDataException("Database mode does not match this server. Use separate data directories.");
            accounts = db.Accounts.ToList(); characters = db.Characters.ToList(); rooms = db.Rooms.ToDictionary(r => r.Id, r => new Room(r, db.RulesVersion));
            if (accounts.Select(a => a.Id).Distinct().Count() != accounts.Count || accounts.Select(a => a.Name).Distinct().Count() != accounts.Count ||
                characters.Select(c => c.Id).Distinct().Count() != characters.Count || characters.Any(c => !accounts.Any(a => a.Id == c.Account)) ||
                rooms.Values.SelectMany(r => r.Saved.Members).GroupBy(m => m.Character).Any(g => g.Count() > 1)) throw new InvalidDataException("Invalid realm ownership.");
            foreach (var room in rooms.Values)
                if (room.Saved.Members.Length is < 1 or > 4 || !room.Saved.Members.Any(m => m.Character == room.Saved.Host) ||
                    room.Saved.Members.Any(m => !characters.Any(c => c.Id == m.Character)) ||
                    room.Saved.Members.Select(m => m.Actor).Distinct().Count() != room.Saved.Members.Length ||
                    room.Saved.Members.Any(m => m.ClientSequence < 0 || m.ClientSequence == long.MaxValue) ||
                    (room.Game is not null && !room.Game.Entities.ToArray().Where(e => e.Kind == EntityKind.Player).Select(e => e.Id.Value).Order().SequenceEqual(room.Saved.Members.Select(m => m.Actor).Order())))
                    throw new InvalidDataException("Invalid room members.");
            foreach (var character in characters)
                if (character.Inventory is { } inventory) Arena.Create(1).WithCarriedItems(inventory);
            // Restarts never replay held movement. Preserve combat/items and command cursors.
            foreach (var room in rooms.Values.Where(r => r.Game is not null)) StopAll(room);
        }
        catch { writerLock.Dispose(); throw; }
    }
    private static void Need(bool condition, int status, string message) { if (!condition) throw new Rejected(status, message); }
    private static string Name(string value)
    {
        Need(value is not null && Regex.IsMatch(value, "\\A[a-zA-Z0-9_-]{3,24}\\z"), 400, "Name requires 3–24 ASCII letters, digits, _ or -."); return value!;
    }
    private static string Hash(string password, string salt) => Convert.ToHexString(Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromHexString(salt), 600000, HashAlgorithmName.SHA256, 32));
    private static (string Salt, string Hash) Secret(string password)
    {
        Need(password is not null && password.Length is >= 12 and <= 128, 400, "Password requires 12–128 characters.");
        string salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)); return (salt, Hash(password!, salt));
    }
    private static bool Matches(string password, string salt, string hash) => CryptographicOperations.FixedTimeEquals(Convert.FromHexString(Hash(password, salt)), Convert.FromHexString(hash));
    private Guid Authorize(string token)
    {
        Need(!failed, 503, "Storage unavailable; restart after repairing storage.");
        Need(sessions.TryGetValue(token, out var s) && s.Until > clock.GetUtcNow(), 401, "Login required."); return s.Account;
    }
    private Character Owned(Guid account, Guid id) => characters.FirstOrDefault(c => c.Id == id && c.Account == account) ?? throw new Rejected(404, "Character not found.");
    public LoginResult Login(Credentials input, bool register)
    {
        string name = Name(input.Username).ToLowerInvariant();
        Need(input.Password is not null && input.Password.Length is >= 12 and <= 128, 401, "Invalid credentials.");
        lock (gate)
        {
            Need(!failed, 503, "Storage unavailable.");
            var account = accounts.FirstOrDefault(a => a.Name == name);
            if (register)
            {
                Need(account is null, 409, "Account unavailable."); Need(accounts.Count < 256, 409, "Realm account limit reached.");
                var secret = Secret(input.Password!); account = new(Guid.NewGuid(), name, secret.Salt, secret.Hash); accounts.Add(account); Save();
            }
            else
            {
                bool valid = Matches(input.Password!, account?.Salt ?? new string('0', 32), account?.Hash ?? new string('0', 64));
                Need(account is not null && valid, 401, "Invalid credentials.");
            }
            foreach (var key in sessions.Where(p => p.Value.Account == account!.Id || p.Value.Until <= clock.GetUtcNow()).Select(p => p.Key).ToArray()) sessions.Remove(key);
            string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)); sessions.Add(token, (account!.Id, clock.GetUtcNow().AddHours(8)));
            return new(token, name);
        }
    }
    public object Logout(string token) { lock (gate) { Authorize(token); sessions.Remove(token); return new { ok = true }; } }
    public CharacterInfo[] Characters(string token) { lock (gate) { var account = Authorize(token); return characters.Where(c => c.Account == account).Select(c => new CharacterInfo(c.Id, c.Name, c.Victories)).ToArray(); } }
    public CharacterInfo CreateCharacter(string token, string name)
    {
        name = Name(name);
        lock (gate)
        {
            var account = Authorize(token); Need(characters.Count(c => c.Account == account) < 4, 409, "Four characters per account.");
            Need(!characters.Any(c => c.Account == account && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)), 409, "Character name already exists.");
            var character = new Character(Guid.NewGuid(), account, name); characters.Add(character); Save(); return new(character.Id, name, 0);
        }
    }
    public OpenCharacter ExportCharacter(string token, Guid id)
    {
        lock (gate)
        {
            var account = Authorize(token);
            Need(Mode == "open", 403, "Personal characters are only available in Open mode.");
            var c = Owned(account, id);
            Need(!rooms.Values.Any(r => r.Saved.Members.Any(m => m.Character == id)), 409, "Leave or close the room before exporting.");
            return new(2, GameSimulation.RulesVersion, "open", c.Name, c.Victories,
                (c.Inventory ?? []).Select(i => new OpenItem(i.Definition, i.Location, i.Slot)).ToArray());
        }
    }
    public CharacterInfo ImportCharacter(string token, OpenCharacter input)
    {
        lock (gate)
        {
            var account = Authorize(token);
            Need(Mode == "open", 403, "Personal characters are only available in Open mode.");
            try { input = OpenCharacterFile.Upgrade(input); }
            catch (InvalidDataException error) { throw new Rejected(400, error.Message); }
            Need(characters.Count(c => c.Account == account) < 4, 409, "Four characters per account.");
            Need(!characters.Any(c => c.Account == account && c.Name.Equals(input.Name, StringComparison.OrdinalIgnoreCase)), 409, "Character name already exists; import never overwrites.");
            var character = new Character(Guid.NewGuid(), account, input.Name, input.Victories,
                input.Inventory!.Select(i => new CarriedItem(new(1), i.Definition, i.Location, i.Slot)).ToArray());
            characters.Add(character); Save(); return new(character.Id, character.Name, character.Victories);
        }
    }
    public object DeleteCharacter(string token, Guid id)
    {
        lock (gate) { var c = Owned(Authorize(token), id); Need(!rooms.Values.Any(r => r.Saved.Members.Any(m => m.Character == id)), 409, "Leave or close the room first."); characters.Remove(c); Save(); return new { ok = true }; }
    }
    public RoomInfo[] Rooms(string token) { lock (gate) { Authorize(token); return rooms.Values.Select(r => new RoomInfo(r.Saved.Id, r.Saved.Name, r.Saved.Members.Length, r.Game is not null, r.Saved.PasswordHash.Length > 0)).ToArray(); } }
    private void Available(Character character)
    {
        Need(!rooms.Values.Any(r => r.Saved.Members.Any(m => characters.Single(c => c.Id == m.Character).Account == character.Account)), 409, "Account already occupies a room; rejoin it or leave first.");
    }
    public RoomView CreateRoom(string token, RoomRequest request)
    {
        string name = Name(request.Name);
        lock (gate)
        {
            var c = Owned(Authorize(token), request.Character); Available(c); Need(rooms.Count < 32, 409, "Realm room limit reached.");
            var secret = string.IsNullOrEmpty(request.Password) ? (Salt: "", Hash: "") : Secret(request.Password);
            var room = new Room(new(Guid.NewGuid(), name, c.Id, secret.Salt, secret.Hash, [new(c.Id, 1)], null));
            rooms.Add(room.Saved.Id, room); Save(); return View(room, c);
        }
    }
    public RoomView Join(string token, Guid id, JoinRequest request)
    {
        lock (gate)
        {
            var c = Owned(Authorize(token), request.Character); var room = Find(id);
            if (!room.Saved.Members.Any(m => m.Character == c.Id))
            {
                bool invited = request.Invitation is not null;
                if (invited) ValidateInvitation(room, request.Invitation!);
                else Need(room.Saved.PasswordHash.Length == 0 || (request.Password is { Length: >= 12 and <= 128 } && Matches(request.Password, room.Saved.Salt, room.Saved.PasswordHash)), 403, "Room password invalid.");
                Available(c); Need(room.Game is null && room.Saved.Members.Length < 4, 409, "Room is started or full.");
                uint actor = Enumerable.Range(1, 4).Select(i => (uint)i).First(i => !room.Saved.Members.Any(m => m.Actor == i));
                room.Saved = room.Saved with { Members = [..room.Saved.Members, new(c.Id, actor)] }; Save();
                if (invited) room.InviteHash = null;
            }
            return View(room, c);
        }
    }
    private Room Find(Guid id) => rooms.GetValueOrDefault(id) ?? throw new Rejected(404, "Room not found.");
    private (Room Room, Character Character) Access(string token, Guid id)
    {
        var account = Authorize(token); var room = Find(id);
        var c = characters.FirstOrDefault(c => c.Account == account && room.Saved.Members.Any(m => m.Character == c.Id));
        Need(c is not null, 403, "Room membership required."); return (room, c!);
    }
    public RoomView State(string token, Guid id) { lock (gate) { var (r, c) = Access(token, id); return View(r, c); } }
    // Only internal serialization may borrow these arrays. Authorization and recipient metadata
    // are evaluated on every read, even when the simulation snapshot is reused.
    internal RoomView StreamState(string token, Guid id) { lock (gate) { var (r, c) = Access(token, id); return View(r, c, shared: true); } }
    private RoomSnapshot? Snapshot(Room room)
    {
        if (room.Game is not { } game) return null;
        if (room.Snapshot is not { } snapshot || !ReferenceEquals(snapshot.Source, game) || snapshot.Tick != game.Tick)
        {
            room.Snapshot = new(game, game.Tick, game.ComputeStateHash(), game.Entities.ToArray(), game.Items.ToArray());
            snapshotBuilds++;
        }
        return room.Snapshot;
    }
    private RoomView View(Room room, Character character, bool shared = false)
    {
        room.Seen[character.Id] = clock.GetUtcNow(); var member = room.Saved.Members.Single(m => m.Character == character.Id);
        var snapshot = Snapshot(room);
        return new(room.Saved.Id, room.Saved.Name, room.Game is not null, room.Saved.Host == character.Id, member.Actor, member.ClientSequence + 1,
            snapshot?.Tick ?? 0, snapshot?.Hash ?? "", room.Saved.Members.Select(m => new MemberInfo(m.Character, characters.Single(c => c.Id == m.Character).Name, m.Actor)).ToArray(),
            snapshot is null ? [] : shared ? snapshot.Entities : (EntityState[])snapshot.Entities.Clone(),
            snapshot is null ? [] : shared ? snapshot.Items : (ItemState[])snapshot.Items.Clone());
    }
    public RoomView Start(string token, Guid id)
    {
        lock (gate)
        {
            var (r, c) = Access(token, id); Need(r.Saved.Host == c.Id, 403, "Only the host can start."); Need(r.Game is null, 409, "Already started.");
            r.Saved = r.Saved with { Members = r.Saved.Members.Select((m, i) => m with { Actor = (uint)i + 1 }).ToArray() };
            r.InviteHash = null;
            r.Game = Arena.Create(r.Saved.Members.Length).WithCarriedItems(r.Saved.Members.SelectMany(m =>
                (characters.Single(x => x.Id == m.Character).Inventory ?? []).Select(item => item with { Owner = new(m.Actor) })).ToArray());
            Save(); return View(r, c);
        }
    }
    public RoomView Input(string token, Guid id, InputRequest input)
    {
        lock (gate)
        {
            var (r, c, _) = QueueInput(token, id, input);
            return View(r, c);
        }
    }
    public InputReceipt AcknowledgeInput(string token, Guid id, InputRequest input)
    {
        lock (gate)
        {
            var (r, _, actor) = QueueInput(token, id, input);
            return new(id, actor, input.Sequence + 1, r.Game!.Tick);
        }
    }
    public InputReceipt BatchInput(string token, Guid id, InputBatchRequest batch)
    {
        lock (gate)
        {
            var (r, c) = Access(token, id); Need(r.Game is not null, 409, "Start the room first.");
            Need(batch?.Inputs is { Length: >= 1 and <= GameSimulation.MaxCommandBatch }, 400, "Expected 1..8 inputs.");
            var inputs = batch!.Inputs;
            var m = r.Saved.Members.Single(m => m.Character == c.Id);
            var game = r.Game!; var e = game.GetEntity(new(m.Actor));
            var cursor = game.CaptureSnapshot().Inputs.Single(i => i.Actor == e.Id);
            Need(cursor.Sequence <= ulong.MaxValue - (ulong)inputs.Length, 409, "Input sequence exhausted.");
            Span<GameCommand> commands = stackalloc GameCommand[inputs.Length];
            for (int i = 0; i < inputs.Length; i++)
            {
                var input = inputs[i];
                Need(input is not null, 400, "Missing input.");
                Need(input!.Sequence > m.ClientSequence && input.Sequence < long.MaxValue &&
                    (i == 0 || input.Sequence == inputs[i - 1].Sequence + 1), 409, "Stale or non-contiguous input sequence.");
                commands[i] = new(game.Tick + 1, cursor.Sequence + (ulong)i + 1, e.Id, e.Region,
                    input.Kind, input.X, input.Y, new(input.Target), new(input.Item));
            }
            var result = game.SubmitBatch(commands);
            Need(result == CommandResult.Accepted, 400, "Command batch rejected: " + result);
            r.Snapshot = null; // Pending commands change the hash without advancing the tick.
            r.Saved = r.Saved with { Members = r.Saved.Members.Select(x => x == m ? x with { ClientSequence = inputs[^1].Sequence } : x).ToArray() };
            var now = clock.GetUtcNow(); r.Seen[c.Id] = now;
            if (inputs.Any(input => input.Kind == CommandKind.SetMove)) r.Moved[c.Id] = now;
            return new(id, m.Actor, inputs[^1].Sequence + 1, game.Tick);
        }
    }
    // Both response formats share authorization, ownership, sequence and lease handling.
    // Caller holds gate, including construction of the response.
    private (Room Room, Character Character, uint Actor) QueueInput(string token, Guid id, InputRequest input)
    {
        var (r, c) = Access(token, id); Need(r.Game is not null, 409, "Start the room first.");
        var m = r.Saved.Members.Single(m => m.Character == c.Id);
        Need(input.Sequence > m.ClientSequence && input.Sequence < long.MaxValue, 409, "Stale input sequence.");
        var e = r.Game!.GetEntity(new(m.Actor)); var cursor = r.Game.CaptureSnapshot().Inputs.Single(i => i.Actor == e.Id);
        var result = r.Game.Submit(new(r.Game.Tick + 1, cursor.Sequence + 1, e.Id, e.Region, input.Kind, input.X, input.Y, new(input.Target), new(input.Item)));
        Need(result == CommandResult.Accepted, 400, "Command rejected: " + result);
        r.Snapshot = null;
        r.Saved = r.Saved with { Members = r.Saved.Members.Select(x => x == m ? x with { ClientSequence = input.Sequence } : x).ToArray() };
        r.Seen[c.Id] = clock.GetUtcNow();
        if (input.Kind == CommandKind.SetMove) r.Moved[c.Id] = clock.GetUtcNow();
        return (r, c, m.Actor);
    }
    private static void Stop(Room r, Member m)
    {
        var game = r.Game!; var e = game.GetEntity(new(m.Actor)); if (!e.IsAlive || (e.MoveX == 0 && e.MoveY == 0)) return;
        var cursor = game.CaptureSnapshot().Inputs.Single(i => i.Actor == e.Id);
        if (game.Submit(new(game.Tick + 1, cursor.Sequence + 1, e.Id, e.Region, CommandKind.SetMove)) != CommandResult.Accepted) throw new InvalidDataException("Cannot stop disconnected actor.");
        r.Snapshot = null;
    }
    private static void StopAll(Room r)
    {
        r.Snapshot = null;
        var s = r.Game!.CaptureSnapshot();
        r.Game = GameSimulation.Restore(s with { PendingCommands = [], Entities = s.Entities.Select(e => e with { MoveX = 0, MoveY = 0 }).ToArray() });
    }
    public void Tick()
    {
        lock (gate)
        {
            if (failed) throw new IOException("Realm storage failed.");
            foreach (var r in rooms.Values.Where(r => r.Game is not null))
            {
                foreach (var m in r.Saved.Members) if (!r.Moved.TryGetValue(m.Character, out var moved) || clock.GetUtcNow() - moved > TimeSpan.FromMilliseconds(500)) Stop(r, m);
                if (r.Seen.Values.Any(t => clock.GetUtcNow() - t < TimeSpan.FromSeconds(15))) r.Game!.Step();
                else StopAll(r);
            }
        }
    }
    public object Checkpoint(string token, Guid id) { lock (gate) { Access(token, id); Save(); return new { ok = true }; } }
    public object Leave(string token, Guid id)
    {
        lock (gate)
        {
            var (r, c) = Access(token, id);
            Need(r.Game is null, 409, "Running room retains characters for reconnect. Host must close it.");
            r.Saved = r.Saved with { Members = r.Saved.Members.Where(m => m.Character != c.Id).ToArray() };
            if (r.Saved.Members.Length == 0) rooms.Remove(id);
            else if (r.Saved.Host == c.Id) { r.InviteHash = null; r.Saved = r.Saved with { Host = r.Saved.Members[0].Character }; }
            Save(); return new { ok = true };
        }
    }
    public object Close(string token, Guid id)
    {
        lock (gate)
        {
            var (r, c) = Access(token, id); Need(r.Saved.Host == c.Id, 403, "Only the host can close.");
            if (r.Game is not null)
            {
                bool victory = r.Game.Entities.ToArray().Where(e => e.Kind == EntityKind.Monster).All(e => !e.IsAlive);
                foreach (var m in r.Saved.Members)
                {
                    int i = characters.FindIndex(x => x.Id == m.Character);
                    characters[i] = characters[i] with { Victories = checked(characters[i].Victories + (victory ? 1 : 0)),
                        Inventory = r.Game.CaptureCarriedItems(new(m.Actor)).Select(item => item with { Owner = new(1) }).ToArray() };
                }
            }
            rooms.Remove(id); Save(); return new { ok = true };
        }
    }
    public void Checkpoint() { lock (gate) Save(); }
    private void Save()
    {
        Need(!failed, 503, "Storage unavailable."); string temporary = file + ".tmp";
        try
        {
            var db = new Database(2, GameSimulation.RulesVersion, accounts.ToArray(), characters.ToArray(), rooms.Values.Select(r => r.Saved with { Game = r.Game is null ? null : SavedGame.Capture(r.Game) }).ToArray(), Mode);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(db);
            if (bytes.Length > 16 * 1024 * 1024) throw new IOException("Realm exceeds storage budget.");
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var output = new FileStream(temporary, options)) { output.Write(bytes); output.Flush(true); }
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, file, true);
        }
        catch { failed = true; throw; }
    }
    public void Dispose() => writerLock.Dispose();
}
