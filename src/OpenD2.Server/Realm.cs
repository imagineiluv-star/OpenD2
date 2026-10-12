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
        public GameSimulation? Game = saved.Game?.Restore(rules, saved.World);
        public readonly HashSet<string> WorldSessions = new();
        public RoomSnapshot? Snapshot;
        public readonly Dictionary<uint, long> Processed = new();
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
    private volatile bool failed;
    // Writes reach the disk in snapshot order. Lock order is always gate -> persistGate; Persist never takes gate.
    private readonly object persistGate = new();
    private long saveSequence, persistedSequence;
    // Test seams, null in production. Between: after the snapshot was taken and the gate released, before the write.
    // Before: inside the write gate, right before the temporary file is created.
    internal Action? BetweenSnapshotAndPersist, BeforePersist;
    private long snapshotBuilds;
    internal long SnapshotBuilds { get { lock (gate) return snapshotBuilds; } }
    public string Mode { get; }
    // The rules every character's progression is checked against, on load and on every change. Preview unless the operator supplies others.
    internal ProgressionTables Tables { get; }
    internal SkillTable SkillTree { get; }
    internal MonsterTable Monsters { get; }
    public Realm(string directory, TimeProvider? time = null, string mode = "realm", ProgressionTables? tables = null, SkillTable? skills = null, MonsterTable? monsters = null)
    {
        if (mode is not ("realm" or "open")) throw new ArgumentException("Unknown online mode.", nameof(mode));
        Mode = mode; Tables = tables ?? ProgressionTables.Preview; SkillTree = skills ?? SkillTable.Preview; Monsters = monsters ?? MonsterTable.Preview;
        // The built-in rooms' monsters must all be kinds of this table, or kills would silently pay nothing. Nothing is created before this.
        Arena.Roster.Validate(Monsters, Arena.Create(4).Entities.ToArray());
        CooperativeWorld.Roster.Validate(Monsters, CooperativeWorld.Create(4).Entities.ToArray());
        clock = time ?? TimeProvider.System;
        Directory.CreateDirectory(directory);
        file = Path.Combine(directory, "realm.json");
        writerLock = new(Path.Combine(directory, "realm.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            if (File.Exists(file) && new FileInfo(file).Length > 16 * 1024 * 1024) throw new InvalidDataException("Realm exceeds storage budget.");
            var db = File.Exists(file) ? JsonSerializer.Deserialize<Database>(File.ReadAllBytes(file)) ?? throw new InvalidDataException("Empty realm.") : new(2, GameSimulation.RulesVersion, [], [], [], Mode);
            if (db.Version is not (1 or 2 or 3) || db.RulesVersion is not (7 or GameSimulation.RulesVersion) || db.Accounts.Length > 256 || db.Characters.Length > 1024 || db.Rooms.Length > 32) throw new InvalidDataException("Unsupported realm version/budget.");
            string? storedMode = db.Version == 1 && db.Mode is null ? "realm" : db.Mode;
            if (storedMode != Mode || (db.Version == 1 && Mode != "realm")) throw new InvalidDataException("Database mode does not match this server. Use separate data directories.");
            foreach (var saved in db.Rooms)
                if (saved.World is not null)
                {
                    if (db.Version != 3) throw new InvalidDataException("World rooms require database version 3.");
                    CooperativeWorld.Resolve(saved.World); // Validate lobbies too, before any write.
                }
            accounts = db.Accounts.ToList(); characters = db.Characters.ToList(); rooms = db.Rooms.ToDictionary(r => r.Id, r => new Room(r, db.RulesVersion));
            if (accounts.Select(a => a.Id).Distinct().Count() != accounts.Count || accounts.Select(a => a.Name).Distinct().Count() != accounts.Count ||
                characters.Select(c => c.Id).Distinct().Count() != characters.Count || characters.Any(c => !accounts.Any(a => a.Id == c.Account)) ||
                rooms.Values.SelectMany(r => r.Saved.Members).GroupBy(m => m.Character).Any(g => g.Count() > 1)) throw new InvalidDataException("Invalid realm ownership.");
            foreach (var room in rooms.Values)
                if (room.Saved.Members.Length is < 1 or > 4 || !room.Saved.Members.Any(m => m.Character == room.Saved.Host) ||
                    room.Saved.Members.Any(m => !characters.Any(c => c.Id == m.Character)) ||
                    room.Saved.Members.Select(m => m.Actor).Distinct().Count() != room.Saved.Members.Length ||
                    room.Saved.Members.Any(m => m.Actor is < 1 or > 4 || m.ClientSequence < 0 || m.ClientSequence == long.MaxValue ||
                        m.RegionEpoch < 0 || m.RegionEpoch > (room.Game?.Tick ?? 0) || (room.Saved.World is null && m.RegionEpoch != 0)) ||
                    (room.Game is not null && !room.Game.Entities.ToArray().Where(e => e.Kind == EntityKind.Player).Select(e => e.Id.Value).Order().SequenceEqual(room.Saved.Members.Select(m => m.Actor).Order())))
                    throw new InvalidDataException("Invalid room members.");
            foreach (var character in characters)
            {
                if (character.Inventory is { } inventory) Arena.Create(1).WithCarriedItems(inventory);
                if (character.Quests is { } quests && !QuestRecords.IsValid(quests)) throw new InvalidDataException("Invalid character quest records.");
                if (character.Progress is { } progress) Progression.Validate(Tables, SkillTree, progress);
            }
            // Experience belongs to a started room and to a character that has a class to spend it on.
            foreach (var room in rooms.Values)
                if (room.Saved.Members.Any(m => m.Experience > 0 && (room.Game is null || characters.Single(c => c.Id == m.Character).Progress is null)))
                    throw new InvalidDataException("Room experience requires a started room and a character with a class.");
            // A credit exists only while the completed shared quest that earned it is still part of the saved room.
            foreach (var room in rooms.Values)
                if (room.Saved.Members.Any(m => m.QuestCredit && (room.Saved.World is null || room.Game is null || room.Game.GetQuest(new(m.Actor)) != QuestStage.Completed)))
                    throw new InvalidDataException("Quest credit requires that member's own completed quest.");
            // Restarts never replay held movement. Preserve combat/items and command cursors.
            foreach (var room in rooms.Values.Where(r => r.Game is not null)) StopAll(room);
        }
        catch { writerLock.Dispose(); throw; }
    }
    private static void Need(bool condition, int status, string message) { if (!condition) throw new Rejected(status, message); }
    // Saturating: Close() must not throw after it has begun updating characters.
    private static QuestRecord[] WithCompletion(QuestRecord[]? records, string world)
    {
        var list = (records ?? []).ToList(); int at = list.FindIndex(q => q.World == world);
        if (at < 0) list.Add(new(world, 1)); else list[at] = list[at] with { Completions = Math.Min(QuestRecords.MaxCompletions, list[at].Completions + 1) };
        return list.ToArray();
    }
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
    // Test seam, null in production: runs just before a login password hash is computed, never while the realm lock is held.
    internal Action? BeforeHashing;
    public LoginResult Login(Credentials input, bool register)
    {
        string name = Name(input.Username).ToLowerInvariant();
        Need(input.Password is not null && input.Password.Length is >= 12 and <= 128, 401, "Invalid credentials.");
        // PBKDF2 (600,000 iterations) takes tens to hundreds of milliseconds. It must not run under the lock that Tick, input
        // and stream reads share, or every room stalls for the duration of each login. Only cheap lookups happen under the
        // lock; the hash runs between two short lock sections, and the second section re-checks everything the first assumed.
        (string Salt, string Hash) secret = ("", ""); bool valid = false;
        if (register)
        {
            lock (gate)
            {
                Need(!failed, 503, "Storage unavailable.");
                Need(!accounts.Any(a => a.Name == name), 409, "Account unavailable."); Need(accounts.Count < 256, 409, "Realm account limit reached.");
            }
            BeforeHashing?.Invoke();
            secret = Secret(input.Password!);
        }
        else
        {
            Account? known;
            lock (gate) { Need(!failed, 503, "Storage unavailable."); known = accounts.FirstOrDefault(a => a.Name == name); }
            BeforeHashing?.Invoke();
            // An unknown name hashes against a dummy value so that existence is not revealed by response time.
            valid = Matches(input.Password!, known?.Salt ?? new string('0', 32), known?.Hash ?? new string('0', 64));
        }
        lock (gate)
        {
            Need(!failed, 503, "Storage unavailable.");
            var account = accounts.FirstOrDefault(a => a.Name == name);
            if (register)
            {
                // Another registration of this name may have finished while this one was hashing.
                Need(account is null, 409, "Account unavailable."); Need(accounts.Count < 256, 409, "Realm account limit reached.");
                account = new(Guid.NewGuid(), name, secret.Salt, secret.Hash); accounts.Add(account); Save();
            }
            else Need(account is not null && valid, 401, "Invalid credentials.");
            foreach (var key in sessions.Where(p => p.Value.Account == account!.Id || p.Value.Until <= clock.GetUtcNow()).Select(p => p.Key).ToArray()) { sessions.Remove(key); foreach (var r in rooms.Values) r.WorldSessions.Remove(key); }
            string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)); sessions.Add(token, (account!.Id, clock.GetUtcNow().AddHours(8)));
            return new(token, name);
        }
    }
    public object Logout(string token) { lock (gate) { Authorize(token); sessions.Remove(token); foreach (var r in rooms.Values) r.WorldSessions.Remove(token); return new { ok = true }; } }
    public CharacterInfo[] Characters(string token) { lock (gate) { var account = Authorize(token); return characters.Where(c => c.Account == account).Select(Info).ToArray(); } }
    private static CharacterInfo Info(Character c) => new(c.Id, c.Name, c.Victories, c.Quests is { Length: > 0 } quests ? quests.ToArray() : null, c.Progress is { } progress ? progress with { Skills = progress.Skills.ToArray() } : null);
    public CharacterInfo CreateCharacter(string token, string name, string? characterClass = null)
    {
        name = Name(name);
        var progress = characterClass is null ? null : Progression.Create(Tables, ParseClass(characterClass));
        lock (gate)
        {
            var account = Authorize(token); Need(characters.Count(c => c.Account == account) < 4, 409, "Four characters per account.");
            Need(!characters.Any(c => c.Account == account && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)), 409, "Character name already exists.");
            var character = new Character(Guid.NewGuid(), account, name, Progress: progress); characters.Add(character); Save(); return Info(character);
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
            return new(4, GameSimulation.RulesVersion, "open", c.Name, c.Victories,
                (c.Inventory ?? []).Select(i => new OpenItem(i.Definition, i.Location, i.Slot)).ToArray(), c.Quests?.ToArray() ?? [], c.Progress);
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
            // The file is checked against this server's rules, not the exporting server's.
            if (input.Progress is { } imported)
                try { Progression.Validate(Tables, SkillTree, imported); }
                catch (InvalidDataException error) { throw new Rejected(400, "Character progress does not match this server's rules: " + error.Message); }
            Need(characters.Count(c => c.Account == account) < 4, 409, "Four characters per account.");
            Need(!characters.Any(c => c.Account == account && c.Name.Equals(input.Name, StringComparison.OrdinalIgnoreCase)), 409, "Character name already exists; import never overwrites.");
            var character = new Character(Guid.NewGuid(), account, input.Name, input.Victories,
                input.Inventory!.Select(i => new CarriedItem(new(1), i.Definition, i.Location, i.Slot)).ToArray(),
                input.Quests!.Length == 0 ? null : input.Quests.ToArray(), input.Progress);
            characters.Add(character); Save(); return Info(character);
        }
    }
    public object DeleteCharacter(string token, Guid id)
    {
        lock (gate) { var c = Owned(Authorize(token), id); Need(!rooms.Values.Any(r => r.Saved.Members.Any(m => m.Character == id)), 409, "Leave or close the room first."); characters.Remove(c); Save(); return new { ok = true }; }
    }
    public WorldContent[] Worlds(string token) { lock (gate) { Authorize(token); return [CooperativeWorld.Content]; } }
    public RoomInfo[] Rooms(string token, bool includeWorlds = false) { lock (gate) { Authorize(token); return rooms.Values
        .Where(r => includeWorlds || r.Saved.World is null)
        .Select(r => new RoomInfo(r.Saved.Id, r.Saved.Name, r.Saved.Members.Length, r.Game is not null, r.Saved.PasswordHash.Length > 0, r.Saved.World)).ToArray(); } }
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
            if (request.World is not null) Need(request.World == CooperativeWorld.Content, 409, "World content ID/hash mismatch.");
            var secret = string.IsNullOrEmpty(request.Password) ? (Salt: "", Hash: "") : Secret(request.Password);
            var room = new Room(new(Guid.NewGuid(), name, c.Id, secret.Salt, secret.Hash, [new(c.Id, 1)], null, request.World));
            if (request.World is not null) room.WorldSessions.Add(token);
            rooms.Add(room.Saved.Id, room); Save(); return View(room, c);
        }
    }
    public RoomView Join(string token, Guid id, JoinRequest request)
    {
        lock (gate)
        {
            var c = Owned(Authorize(token), request.Character); var room = Find(id);
            Need(request.World == room.Saved.World, 409, "Matching world content is required to join or reconnect.");
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
            if (room.Saved.World is not null) room.WorldSessions.Add(token);
            return View(room, c);
        }
    }
    private Room Find(Guid id) => rooms.GetValueOrDefault(id) ?? throw new Rejected(404, "Room not found.");
    private (Room Room, Character Character) Access(string token, Guid id)
    {
        var account = Authorize(token); var room = Find(id);
        var c = characters.FirstOrDefault(c => c.Account == account && room.Saved.Members.Any(m => m.Character == c.Id));
        Need(c is not null, 403, "Room membership required.");
        Need(room.Saved.World is null || room.WorldSessions.Contains(token), 409, "Rejoin with matching world content first.");
        return (room, c!);
    }
    public RoomView State(string token, Guid id) { lock (gate) { var (r, c) = Access(token, id); return View(r, c); } }
    // Only internal serialization may borrow these arrays. Authorization and recipient metadata
    // are evaluated on every read, even when the simulation snapshot is reused.
    internal RoomView StreamState(string token, Guid id, bool interest = false, RoomView? previous = null, bool inputProgress = false, bool world = false)
    {
        RoomView view;
        lock (gate)
        {
            var (r, c) = Access(token, id);
            Need(r.Saved.World is null || world, 409, "World rooms require the v5 stream protocol.");
            view = View(r, c, shared: true);
            if (inputProgress) view = view with { ProcessedSequence = r.Processed.GetValueOrDefault(view.Actor) };
        }
        // Filtering is connection-specific; immutable full snapshots remain shared.
        return interest ? RoomInterest.Project(view, previous) : view;
    }
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
        var view = new RoomView(room.Saved.Id, room.Saved.Name, room.Game is not null, room.Saved.Host == character.Id, member.Actor, member.ClientSequence + 1,
            snapshot?.Tick ?? 0, snapshot?.Hash ?? "", room.Saved.Members.Select(m => new MemberInfo(m.Character, characters.Single(c => c.Id == m.Character).Name, m.Actor)).ToArray(),
            snapshot is null ? [] : shared ? snapshot.Entities : (EntityState[])snapshot.Entities.Clone(),
            snapshot is null ? [] : shared ? snapshot.Items : (ItemState[])snapshot.Items.Clone(),
            World: room.Saved.World is { } content ? new(content, room.Game?.GetQuestProgress(new(member.Actor)) ?? new(QuestStage.Available, 0, CooperativeWorld.Definition.QuestTargets.Length), member.RegionEpoch) : null,
            Experience: character.Progress is null ? null : member.Experience);
        return view;
    }
    public RoomView Start(string token, Guid id)
    {
        lock (gate)
        {
            var (r, c) = Access(token, id); Need(r.Saved.Host == c.Id, 403, "Only the host can start."); Need(r.Game is null, 409, "Already started.");
            r.Saved = r.Saved with { Members = r.Saved.Members.Select((m, i) => m with { Actor = (uint)i + 1 }).ToArray() };
            r.InviteHash = null;
            var start = r.Saved.World is null ? Arena.Create(r.Saved.Members.Length) : CooperativeWorld.Create(r.Saved.Members.Length);
            // Characters with a class start with the life and mana their level and attributes give; the rest keep the default pool.
            var pools = r.Saved.Members.Select(m => (m, c: characters.Single(x => x.Id == m.Character))).Where(t => t.c.Progress is not null)
                .Select(t => { var pool = Progression.Resources(Tables, t.c.Progress!); return (new EntityId(t.m.Actor), pool.Life, pool.Mana); }).ToArray();
            r.Game = (pools.Length == 0 ? start : start.WithPlayerResources(pools)).WithCarriedItems(r.Saved.Members.SelectMany(m =>
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
                ValidateInputRegion(r, m, e, input!);
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
        ValidateInputRegion(r, m, e, input);
        Need(cursor.Sequence < ulong.MaxValue, 409, "Input sequence exhausted.");
        var result = r.Game.Submit(new(r.Game.Tick + 1, cursor.Sequence + 1, e.Id, e.Region, input.Kind, input.X, input.Y, new(input.Target), new(input.Item)));
        Need(result == CommandResult.Accepted, 400, "Command rejected: " + result);
        r.Snapshot = null;
        r.Saved = r.Saved with { Members = r.Saved.Members.Select(x => x == m ? x with { ClientSequence = input.Sequence } : x).ToArray() };
        r.Seen[c.Id] = clock.GetUtcNow();
        if (input.Kind == CommandKind.SetMove) r.Moved[c.Id] = clock.GetUtcNow();
        return (r, c, m.Actor);
    }
    private static void ValidateInputRegion(Room room, Member member, EntityState actor, InputRequest input)
    {
        if (room.Saved.World is not null)
            Need(input.Region == actor.Region.Value && input.RegionEpoch == member.RegionEpoch, 409,
                "World input belongs to a stale region visit. Refresh room state.");
        else Need(input.Region is null && input.RegionEpoch is null, 400, "Arena input must omit world region fields.");
    }
    private static void Stop(Room r, Member m)
    {
        var game = r.Game!; var e = game.GetEntity(new(m.Actor)); if (!e.IsAlive || (e.MoveX == 0 && e.MoveY == 0)) return;
        var cursor = game.CaptureSnapshot().Inputs.Single(i => i.Actor == e.Id);
        if (game.Submit(new(game.Tick + 1, cursor.Sequence + 1, e.Id, e.Region, CommandKind.SetMove)) != CommandResult.Accepted) throw new InvalidDataException("Cannot stop disconnected actor.");
        r.Snapshot = null;
    }
    // Every accepted client command targets the next tick. Once that tick is stepped,
    // or its pending commands are deliberately discarded, none may be replayed.
    // This is a consumed cursor, not proof that an attack/move succeeded.
    private static void MarkProcessed(Room r)
    {
        foreach (var member in r.Saved.Members) r.Processed[member.Actor] = member.ClientSequence;
    }
    private static void StopAll(Room r)
    {
        r.Snapshot = null;
        var s = r.Game!.CaptureSnapshot();
        r.Game = GameSimulation.Restore(s with { PendingCommands = [], Entities = s.Entities.Select(e => e with { MoveX = 0, MoveY = 0 }).ToArray() });
        MarkProcessed(r);
    }
    public void Tick()
    {
        lock (gate)
        {
            if (failed) throw new IOException("Realm storage failed.");
            foreach (var r in rooms.Values.Where(r => r.Game is not null))
            {
                foreach (var m in r.Saved.Members) if (!r.Moved.TryGetValue(m.Character, out var moved) || clock.GetUtcNow() - moved > TimeSpan.FromMilliseconds(500)) Stop(r, m);
                if (r.Seen.Values.Any(t => clock.GetUtcNow() - t < TimeSpan.FromSeconds(15)))
                {
                    r.Game!.Step();
                    var completed = new HashSet<uint>();
                    foreach (var change in r.Game.Events)
                        if (change.Kind == SimulationEventKind.RegionChanged)
                        {
                            var member = r.Saved.Members.Single(m => m.Actor == change.Actor.Value);
                            r.Moved.Remove(member.Character); // A lease never follows an actor through a portal.
                            r.Saved = r.Saved with { Members = r.Saved.Members.Select(m => m == member ? m with { RegionEpoch = r.Game.Tick } : m).ToArray() };
                        }
                        else if (change.Kind == SimulationEventKind.QuestChanged && change.Value == (int)QuestStage.Completed) completed.Add(change.Actor.Value);
                        else if (change.Kind == SimulationEventKind.Died) AwardExperience(r, change);
                    // Quests are personal: only the members whose own turn-in completed on this tick are credited.
                    if (completed.Count > 0 && r.Saved.World is not null)
                        r.Saved = r.Saved with { Members = r.Saved.Members.Select(m => completed.Contains(m.Actor) ? m with { QuestCredit = true } : m).ToArray() };
                    MarkProcessed(r);
                }
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
                // Arena: the room's monsters are all dead. World: each member's own quest is completed (quests are personal).
                bool arenaVictory = r.Game.World is null && r.Game.Entities.ToArray().Where(e => e.Kind == EntityKind.Monster).All(e => !e.IsAlive);
                foreach (var m in r.Saved.Members)
                {
                    bool victory = r.Game.World is not null ? r.Game.GetQuest(new(m.Actor)) == QuestStage.Completed : arenaVictory;
                    int i = characters.FindIndex(x => x.Id == m.Character);
                    characters[i] = characters[i] with { Victories = checked(characters[i].Victories + (victory ? 1 : 0)),
                        Inventory = r.Game.CaptureCarriedItems(new(m.Actor)).Select(item => item with { Owner = new(1) }).ToArray(),
                        Quests = m.QuestCredit && r.Saved.World is { } world ? WithCompletion(characters[i].Quests, world.Id) : characters[i].Quests,
                        Progress = m.Experience > 0 && characters[i].Progress is { } progress ? Progression.Gain(Tables, progress, m.Experience).Progress : characters[i].Progress };
                }
            }
            rooms.Remove(id); Save(); return new { ok = true };
        }
    }
    // The periodic checkpoint (every 25 ticks, and at shutdown) takes its snapshot under the lock but writes it outside:
    // serializing is about a millisecond, whereas fsync can take longer than the 40 ms tick on a slow disk and must not stall
    // Tick, input and stream reads. The call still returns only after its snapshot (or a newer one) is on disk.
    public void Checkpoint()
    {
        (byte[] Bytes, long Sequence) snapshot;
        lock (gate) { Need(!failed, 503, "Storage unavailable."); snapshot = Snapshot(); }
        BetweenSnapshotAndPersist?.Invoke();
        Persist(snapshot.Bytes, snapshot.Sequence);
    }
    // Mutations (accounts, characters, room lifecycle) stay synchronous under the lock: the caller's reply means durable.
    private void Save()
    {
        Need(!failed, 503, "Storage unavailable.");
        var snapshot = Snapshot();
        Persist(snapshot.Bytes, snapshot.Sequence);
    }
    // Caller holds gate.
    private (byte[] Bytes, long Sequence) Snapshot()
    {
        try
        {
            var db = new Database(rooms.Values.Any(r => r.Saved.World is not null) ? 3 : 2, GameSimulation.RulesVersion, accounts.ToArray(), characters.ToArray(), rooms.Values.Select(r => r.Saved with { Game = r.Game is null ? null : SavedGame.Capture(r.Game) }).ToArray(), Mode);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(db);
            if (bytes.Length > 16 * 1024 * 1024) throw new IOException("Realm exceeds storage budget.");
            return (bytes, ++saveSequence);
        }
        catch { failed = true; throw; }
    }
    private void Persist(byte[] bytes, long sequence)
    {
        lock (persistGate)
        {
            // A newer snapshot already reached the disk (a mutation saved while this one waited): never write back older state.
            if (sequence <= persistedSequence) return;
            string temporary = file + ".tmp";
            try
            {
                BeforePersist?.Invoke();
                var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (var output = new FileStream(temporary, options)) { output.Write(bytes); output.Flush(true); }
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Move(temporary, file, true);
                persistedSequence = sequence;
            }
            catch { failed = true; throw; }
        }
    }
    // An in-flight checkpoint write finishes before the data directory lock is released.
    public void Dispose() { lock (persistGate) writerLock.Dispose(); }
}
