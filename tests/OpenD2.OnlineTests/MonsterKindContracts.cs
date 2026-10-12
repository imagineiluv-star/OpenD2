using System.Text.Json;
using OpenD2.Core;
using OpenD2.Online;
using OpenD2.Server;

// Monster kinds on the server: the built-in rooms must be covered by the table the server runs on, a kill pays the value of the
// monster's kind (not its health), the table is listed to clients, and the room text names what stands in the player's region.
internal static class MonsterKindContracts
{
    private const string Password = "monster-kind-contract-password";
    private static readonly WorldContent Content = CooperativeWorld.Content;
    private static void Check(bool value, string reason) { if (!value) throw new Exception("Monster kinds: " + reason); }
    private static void Reject(int status, string reason, Action action)
    {
        try { action(); } catch (Rejected error) when (error.Status == status) { return; }
        throw new Exception($"Monster kinds: expected {status} for {reason}");
    }
    private static MonsterTable Table(int grunt, int brute) => new([new("preview-grunt", "Grunt", 1, grunt), new("preview-brute", "Brute", 2, brute)], "contract", false);

    internal static void Run()
    {
        Coverage();
        Labels();
        foreach (string mode in new[] { "realm", "open" }) { Listing(mode); KillValue(mode); }
        Console.WriteLine("MONSTER KINDS PASS built-in coverage, refusal of an uncovered table, kind-valued kills, table listing and room labels");
    }

    private static void Coverage()
    {
        Arena.Roster.Validate(MonsterTable.Preview, Arena.Create(4).Entities.ToArray());
        CooperativeWorld.Roster.Validate(MonsterTable.Preview, CooperativeWorld.Create(4).Entities.ToArray());
        Check(Arena.Roster.Kinds.Count == 2 && CooperativeWorld.Roster.Kinds.Count == 2, "both built-in rooms have two monsters");
        // A table that lacks a kind (for example an original table with these preview worlds) stops the server before it touches storage.
        string folder = Path.Combine(Path.GetTempPath(), "opend2-monsters-" + Guid.NewGuid().ToString("N"));
        try
        {
            var original = new MonsterTable([new("zombie1", "zombie", 3, 6)], "original", true);
            try { new Realm(folder, mode: "open", monsters: original).Dispose(); throw new Exception("Monster kinds: an uncovered table was accepted"); }
            catch (InvalidDataException error) { Check(error.Message.Contains("preview-"), "the refusal names the missing kind"); }
            Check(!Directory.Exists(folder), "a refused table creates no storage");
            new Realm(folder, monsters: new MonsterTable([new("PREVIEW-GRUNT", "g", 1, 1), new("Preview-Brute", "b", 1, 1)], "case", false)).Dispose();
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    private static void Listing(string mode)
    {
        string folder = Path.Combine(Path.GetTempPath(), "opend2-monsters-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var realm = new Realm(folder, mode: mode);
            var token = realm.Login(new("monster-list", Password), true).Token;
            var kinds = realm.MonsterKinds(token);
            Check(kinds.Select(k => k.Code).SequenceEqual(["preview-brute", "preview-grunt"]), "the table is listed in code order");
            Check(kinds.Single(k => k.Code == "preview-grunt") == new MonsterInfo("preview-grunt", "Preview grunt", 1, 60) &&
                  kinds.Single(k => k.Code == "preview-brute") == new MonsterInfo("preview-brute", "Preview brute", 2, 100), "a listing carries name key, level and experience");
            Reject(401, "listing without a login", () => realm.MonsterKinds("nope"));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    // The same fixture as CharacterProgressContracts, reduced to one classed member beside the Field monsters, under a table whose
    // values are not the preview ones: the member is paid the table's value for each kind, and nothing for a kind worth nothing.
    private static void KillValue(string mode)
    {
        foreach (var (grunt, brute, expected) in new[] { (7u, 0u, 7u), (0u, 9u, 9u), (7u, 9u, 16u) })
        {
            string folder = Path.Combine(Path.GetTempPath(), "opend2-monsters-" + Guid.NewGuid().ToString("N")), file = Path.Combine(folder, "realm.json");
            var table = Table((int)grunt, (int)brute); var time = new TestTime(); Guid hero, room;
            try
            {
                string token;
                using (var realm = new Realm(folder, time, mode, monsters: table))
                {
                    token = realm.Login(new("monster-kill", Password), true).Token;
                    hero = realm.CreateCharacter(token, "KindHero", "Barbarian").Id;
                    room = realm.CreateRoom(token, new(hero, "Kinds", "", Content)).Id;
                    realm.Start(token, room); realm.Checkpoint(token, room);
                }
                var db = JsonSerializer.Deserialize<Database>(File.ReadAllBytes(file))!;
                var snapshot = db.Rooms[0].Game!.Restore(GameSimulation.RulesVersion, Content).CaptureSnapshot();
                var entities = snapshot.Entities.Select(e => e.Id.Value switch
                {
                    1 => e with { Region = new(2), Position = new(3300, 2304) },
                    100 => e with { Health = 1 },
                    101 => e with { Health = 1, Position = new(3500, 2500) },
                    _ => e
                }).ToArray();
                File.WriteAllBytes(file, JsonSerializer.SerializeToUtf8Bytes(db with { Rooms = [db.Rooms[0] with { Game = SavedGame.Capture(GameSimulation.Restore(snapshot with { Entities = entities })) }] }));
                using (var realm = new Realm(folder, time, mode, monsters: table))
                {
                    token = realm.Login(new("monster-kill", Password), false).Token;
                    realm.Join(token, room, new(hero, "", World: Content));
                    foreach (uint target in new uint[] { 100, 101 })
                        for (int wait = 0; wait < 80 && realm.StreamState(token, room, world: true).Entities.Single(e => e.Id.Value == target).IsAlive; wait++)
                        {
                            var view = realm.StreamState(token, room, inputProgress: true, world: true); var self = view.Entities.Single(e => e.Id.Value == 1);
                            if (self.AttackCooldown == 0 && self.HitStun == 0)
                                realm.Input(token, room, new(view.NextSequence, CommandKind.Attack, Target: target, Region: 2, RegionEpoch: view.World!.RegionEpoch));
                            realm.Tick();
                        }
                    Check(realm.StreamState(token, room, world: true).Entities.Where(e => e.Kind == EntityKind.Monster).All(e => !e.IsAlive), "both monsters died");
                    realm.Checkpoint(token, room);
                }
                uint paid = JsonSerializer.Deserialize<Database>(File.ReadAllBytes(file))!.Rooms[0].Members.Single().Experience;
                Check(paid == expected, $"grunt {grunt} + brute {brute} paid {paid}, expected {expected}");
            }
            finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        }
    }

    private static void Labels()
    {
        var roster = new MonsterRoster([(100, "preview-grunt"), (101, "preview-brute"), (102, "PREVIEW-BRUTE")]);
        MonsterInfo[] kinds = [new("preview-grunt", "Preview grunt", 1, 60), new("preview-brute", "Preview brute", 2, 100)];
        EntityState Monster(uint id, uint region = 1, int health = 10) => new(new(id), new(region), new(512, 512), Kind: EntityKind.Monster, Health: health, MaxHealth: 60);
        RoomView View(uint actor, params EntityState[] entities) => new(Guid.NewGuid(), "r", true, true, actor, 1, 0, "", [], entities, []);
        var me = new EntityState(new(1), new(1), new(384, 384));
        string text = MonsterLabels.Describe(View(1, me, Monster(100, health: 60), Monster(101, health: 0), Monster(102, 2), Monster(103), Monster(104, health: 5)), roster, kinds);
        Check(text == "Preview grunt Lv 1 60/60 · Monster 103 10/60 · Monster 104 5/60", "living monsters of the viewer's region, by id, with the kind's name and level: " + text);
        Check(MonsterLabels.Describe(View(1, me, Monster(102, 1, 7)), roster, kinds) == "Preview brute Lv 2 7/60", "a kind code matches without regard to case");
        Check(MonsterLabels.Describe(View(1, me with { Region = new(2) }, Monster(100, 1), Monster(102, 2)), roster, kinds) == "Preview brute Lv 2 10/60", "another region shows its own monsters");
        Check(MonsterLabels.Describe(View(1, me), roster, kinds) == "" && MonsterLabels.Describe(View(2, me, Monster(100)), roster, kinds) == "", "no monsters or no viewer entity shows nothing");
        Check(MonsterLabels.Describe(View(1, me, Monster(100)), roster, []) == "Monster 100 10/60", "an empty table still shows the monster");
        var crowd = Enumerable.Range(0, 20).Select(i => Monster((uint)(200 + i))).ToArray();
        Check(MonsterLabels.Describe(View(1, [me, .. crowd]), roster, kinds).Split(" · ").Length == MonsterLabels.MaxListed, "the list is capped");
        Throws<ArgumentNullException>(() => MonsterLabels.Describe(null!, roster, kinds));
    }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Monster kinds: expected " + typeof(T).Name); }
}
