using System.Text.Json;
using OpenD2.Core;
using OpenD2.Online;
using OpenD2.Server;

// Server side of the Diablo II style progression: class choice, life and mana derived at room start, experience from kills shared
// inside the monster's region, settlement when the host closes the room, point spending, persistence and the Open file (format 4).
// All numbers are the built-in preview tables; nothing here claims equality with Diablo II data.
internal static class CharacterProgressContracts
{
    private const string Password = "character-progress-contract-password";
    private static readonly ProgressionTables Tables = ProgressionTables.Preview;
    private static readonly WorldContent Content = CooperativeWorld.Content;
    private static readonly string?[] Classes = ["Amazon", "sorceress", null, "Druid"];
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static void Check(bool value, string reason) { if (!value) throw new Exception("Character progress: " + reason); }
    private static void Reject(int status, string reason, Action action)
    {
        try { action(); } catch (Rejected error) when (error.Status == status) { return; }
        throw new Exception($"Character progress: expected {status} for {reason}");
    }
    private static void Corrupt(string reason, Action action)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new Exception("Character progress: realm accepted " + reason);
    }
    private static Database Read(string file) => JsonSerializer.Deserialize<Database>(File.ReadAllBytes(file))!;
    private static void Edit(string file, Func<Database, Database> edit) => File.WriteAllBytes(file, JsonSerializer.SerializeToUtf8Bytes(edit(Read(file))));
    private static EntityState Actor(RoomView view, uint id) => view.Entities.Single(e => e.Id.Value == id);

    internal static void Run()
    {
        Wire();
        foreach (string mode in new[] { "realm", "open" })
        {
            string folder = Path.Combine(Path.GetTempPath(), "opend2-progress-" + Guid.NewGuid().ToString("N"));
            try { Play(folder, mode); Console.WriteLine($"CHARACTER PROGRESS PASS {mode} class creation, derived resources, region-shared experience, settlement, spending and tamper rejection"); }
            finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        }
        OpenFiles();
    }

    private static void Wire()
    {
        // Older clients and servers must see the same JSON for a character without a class.
        Check(JsonSerializer.Serialize(new NameRequest("Plain"), Web) == "{\"name\":\"Plain\"}", "a name-only create request keeps its old shape");
        var legacy = new CharacterInfo(Guid.NewGuid(), "Plain", 2);
        Check(!JsonSerializer.Serialize(legacy, Web).Contains("progress") && !JsonSerializer.Serialize(legacy, Web).Contains("quests"), "a character without a class keeps its old shape");
        var progress = Progression.AddStat(Progression.Gain(Tables, Progression.Create(Tables, CharacterClass.Paladin), 5000).Progress, CharacterStat.Energy);
        var info = new CharacterInfo(Guid.NewGuid(), "Classed", 0, null, progress);
        var round = JsonSerializer.Deserialize<CharacterInfo>(JsonSerializer.Serialize(info, Web), Web)!;
        Check(round.Progress is not null && JsonSerializer.Serialize(round, Web) == JsonSerializer.Serialize(info, Web) && round.Progress.Class == CharacterClass.Paladin, "progress survives the wire");
        Progression.Validate(Tables, SkillTable.Preview, round.Progress!);
    }

    private static void Play(string folder, string mode)
    {
        string file = Path.Combine(folder, "realm.json"); var time = new TestTime();
        string[] names = ["prog-a", "prog-b", "prog-c", "prog-d"]; var characters = new Guid[4]; Guid room;
        string[] tokens;
        using (var realm = new Realm(folder, time, mode))
        {
            tokens = names.Select(n => realm.Login(new(n, Password), true).Token).ToArray();
            Reject(400, "unknown class", () => realm.CreateCharacter(tokens[0], "Wizard1", "Wizard"));
            foreach (string bad in new[] { "", "3", " Amazon", "Ama zon", "Amazon1", "AmazonAmazonAmazonAmazon", "-1" }) Reject(400, "class '" + bad + "'", () => realm.CreateCharacter(tokens[0], "BadClass", bad));
            Check(realm.Characters(tokens[0]).Length == 0, "rejected classes created nothing");
            for (int i = 0; i < 4; i++)
            {
                var created = realm.CreateCharacter(tokens[i], "Prog" + "ABCD"[i], Classes[i]); characters[i] = created.Id;
                Check(created.Progress is null == (Classes[i] is null), "only a class gives progress");
                if (created.Progress is { } p) Check(p == Progression.Create(Tables, Enum.Parse<CharacterClass>(Classes[i]!, true)), "a new character starts at level 1");
            }
            Check(realm.Characters(tokens[0]).Single().Progress!.Class == CharacterClass.Amazon && realm.Characters(tokens[1]).Single().Progress!.Class == CharacterClass.Sorceress, "class names are case-insensitive");
            // The client learns the skill table from the server instead of assuming it.
            var table = realm.Skills(tokens[0]);
            Check(table.Length == SkillTable.Preview.All.Count && table.Select(s => s.Id).SequenceEqual(table.Select(s => s.Id).Order()), "the skill table lists every skill in ID order");
            var second = table.Single(s => s.Id == 102);
            Check(second.Class == "Amazon" && second.RequiredLevel == 6 && second.MaxLevel == 20 && second.Requires.SequenceEqual([101]) && table.Single(s => s.Id == 201).Class == "Sorceress", "a skill entry carries class, requirements and prerequisites");
            Reject(401, "listing skills without a login", () => realm.Skills("nope"));
        }
        // The Amazon is 20 experience short of level 2 (fixture written through the production document format).
        Edit(file, db => db with { Characters = db.Characters.Select(c => c.Id == characters[0] ? c with { Progress = Progression.Gain(Tables, c.Progress!, 480).Progress } : c).ToArray() });

        using (var realm = new Realm(folder, time, mode))
        {
            tokens = names.Select(n => realm.Login(new(n, Password), false).Token).ToArray();
            room = realm.CreateRoom(tokens[0], new(characters[0], "Progress", "", Content)).Id;
            for (int i = 1; i < 4; i++) realm.Join(tokens[i], room, new(characters[i], "", World: Content));
            Reject(409, "spending while in a room", () => realm.SpendStat(tokens[0], characters[0], new("Strength")));
            var view = realm.Start(tokens[0], room);
            var amazon = Progression.Resources(Tables, realm.Characters(tokens[0]).Single().Progress!);
            Check(amazon == new CharacterResources(50, 15, 84), "the Amazon's preview pool");
            foreach (var (index, cls) in new[] { (0, CharacterClass.Amazon), (1, CharacterClass.Sorceress), (3, CharacterClass.Druid) })
            {
                var pool = Progression.Resources(Tables, Progression.Create(Tables, cls)); var entity = Actor(view, (uint)index + 1);
                Check(entity.Health == pool.Life && entity.MaxHealth == pool.Life && entity.Mana == pool.Mana && entity.MaxMana == pool.Mana, $"{cls} starts with its derived life and mana");
            }
            var plain = Actor(view, 3); Check(plain.MaxHealth == 100 && plain.MaxMana == 60, "a character without a class keeps the default pool");
            Check(Actor(view, 2).MaxMana == 35 && Actor(view, 1).MaxMana == 15, "mana follows the class energy");
            realm.Checkpoint(tokens[0], room);
        }
        // One kill away: A, C and D stand in the Field next to the last-hit monster, B is in the Camp, D is dead, C has no class.
        Edit(file, db =>
        {
            var game = db.Rooms[0].Game!.Restore(GameSimulation.RulesVersion, Content); var snapshot = game.CaptureSnapshot();
            var entities = snapshot.Entities.Select(e => e.Id.Value switch
            {
                1 => e with { Region = new(2), Position = new(3300, 2304) },
                3 => e with { Region = new(2), Position = new(3300, 2560) },
                4 => e with { Region = new(2), Position = new(3300, 2816), Health = 0, Mode = MonsterMode.Dead },
                100 => e with { Health = 1 },
                101 => e with { Health = 1, Position = new(3500, 2500) },
                _ => e
            }).ToArray();
            return db with { Rooms = [db.Rooms[0] with { Game = SavedGame.Capture(GameSimulation.Restore(snapshot with { Entities = entities })) }] };
        });
        byte[] playing;
        using (var realm = new Realm(folder, time, mode))
        {
            tokens = names.Select(n => realm.Login(new(n, Password), false).Token).ToArray();
            for (int i = 0; i < 4; i++) realm.Join(tokens[i], room, new(characters[i], "", World: Content));
            var view = realm.StreamState(tokens[0], room, inputProgress: true, world: true);
            realm.Input(tokens[0], room, new(view.NextSequence, CommandKind.Attack, Target: 100, Region: 2, RegionEpoch: view.World!.RegionEpoch));
            realm.Tick();
            Check(!Actor(realm.StreamState(tokens[0], room, world: true), 100).IsAlive, "the first monster died");
            // The second kill is a different kind and pays its own value once the attack cooldown is over.
            for (int wait = 0; wait < 60 && Actor(realm.StreamState(tokens[0], room, world: true), 101).IsAlive; wait++)
            {
                view = realm.StreamState(tokens[0], room, inputProgress: true, world: true);
                var self = Actor(view, 1);
                if (self.AttackCooldown == 0 && self.HitStun == 0)
                    realm.Input(tokens[0], room, new(view.NextSequence, CommandKind.Attack, Target: 101, Region: 2, RegionEpoch: view.World!.RegionEpoch));
                realm.Tick();
            }
            Check(!Actor(realm.StreamState(tokens[0], room, world: true), 101).IsAlive, "the second monster died");
            realm.Checkpoint(tokens[0], room);
            Check(realm.Characters(tokens[0]).Single().Progress!.Experience == 480, "experience is room state until the host closes the room");
        }
        playing = File.ReadAllBytes(file);
        var members = Read(file).Rooms[0].Members;
        uint ExperienceOf(int index) => members.Single(m => m.Character == characters[index]).Experience;
        Check(MonsterTable.Preview.Get("preview-grunt").Experience == 60 && MonsterTable.Preview.Get("preview-brute").Experience == 100, "the preview kinds are worth 60 and 100");
        Check(ExperienceOf(0) == 160, "the living classed member beside the monsters is paid the value of each kind that died");
        Check(ExperienceOf(1) == 0, "a member in another region is not paid");
        Check(ExperienceOf(2) == 0, "a character without a class earns nothing");
        Check(ExperienceOf(3) == 0, "a dead member is not paid");

        // Experience in a restarted room is part of the room, so the close after the restart still settles it.
        using (var realm = new Realm(folder, time, mode))
        {
            tokens = names.Select(n => realm.Login(new(n, Password), false).Token).ToArray();
            for (int i = 0; i < 4; i++) realm.Join(tokens[i], room, new(characters[i], "", World: Content));
            Reject(403, "close by a guest", () => realm.Close(tokens[1], room));
            realm.Close(tokens[0], room);
            var a = realm.Characters(tokens[0]).Single().Progress!;
            Check(a.Level == 2 && a.Experience == 640 && a.StatPoints == 5 && a.SkillPoints == 1, "closing pays the experience and the level-up points");
            Progression.Validate(Tables, SkillTable.Preview, a);
            foreach (int i in new[] { 1, 3 })
            {
                var other = realm.Characters(tokens[i]).Single().Progress!;
                Check(other.Level == 1 && other.Experience == 0 && other.StatPoints == 0 && other.SkillPoints == 0, "unpaid members are unchanged");
            }
            Check(realm.Characters(tokens[2]).Single().Progress is null, "a character without a class stays without one");
        }

        using (var realm = new Realm(folder, time, mode))
        {
            tokens = names.Select(n => realm.Login(new(n, Password), false).Token).ToArray();
            // A level-up changes the pool at the next start; spending changes it again.
            var arena = realm.CreateRoom(tokens[0], new(characters[0], "Arena", "")).Id;
            var level2 = Progression.Resources(Tables, realm.Characters(tokens[0]).Single().Progress!);
            Check(level2 == new CharacterResources(52, 16, 85), "level 2 pool follows the quarter-point rates");
            Check(Actor(realm.Start(tokens[0], arena), 1).MaxHealth == 52, "the level-up reaches the next room");
            Reject(409, "spending in a room", () => realm.SpendSkill(tokens[0], characters[0], new(101)));
            realm.Close(tokens[0], arena);

            Reject(401, "spending without a login", () => realm.SpendStat("nope", characters[0], new("Strength")));
            Reject(404, "spending on another account's character", () => realm.SpendStat(tokens[1], characters[0], new("Strength")));
            Reject(409, "spending without a class", () => realm.SpendStat(tokens[2], characters[2], new("Strength")));
            Reject(409, "a skill without a class", () => realm.SpendSkill(tokens[2], characters[2], new(101)));
            Reject(409, "no points to spend", () => realm.SpendStat(tokens[1], characters[1], new("Strength")));
            foreach (string bad in new[] { "Luck", "1", "", " Strength", "Strength1", "StrengthStrengthStrength" }) Reject(400, "attribute '" + bad + "'", () => realm.SpendStat(tokens[0], characters[0], new(bad)));
            Reject(400, "a missing request", () => realm.SpendStat(tokens[0], characters[0], null!));
            Reject(400, "a missing skill request", () => realm.SpendSkill(tokens[0], characters[0], null!));
            Reject(400, "an unknown skill", () => realm.SpendSkill(tokens[0], characters[0], new(99999)));
            Reject(409, "another class's skill", () => realm.SpendSkill(tokens[0], characters[0], new(201)));
            Reject(409, "a skill above the character's level", () => realm.SpendSkill(tokens[0], characters[0], new(102)));
            Check(realm.Characters(tokens[0]).Single().Progress!.StatPoints == 5, "rejected spends cost nothing");

            var spent = realm.SpendStat(tokens[0], characters[0], new("vitality")); spent = realm.SpendStat(tokens[0], characters[0], new("Vitality"));
            spent = realm.SpendStat(tokens[0], characters[0], new("ENERGY")); spent = realm.SpendStat(tokens[0], characters[0], new("Strength")); spent = realm.SpendStat(tokens[0], characters[0], new("Dexterity"));
            var p = spent.Progress!;
            Check(p.StatPoints == 0 && p.Vitality == 22 && p.Energy == 16 && p.Strength == 21 && p.Dexterity == 26, "points land on the chosen attributes");
            Reject(409, "a sixth attribute point", () => realm.SpendStat(tokens[0], characters[0], new("Strength")));
            spent = realm.SpendSkill(tokens[0], characters[0], new(101));
            Check(spent.Progress!.SkillLevel(101) == 1 && spent.Progress.SkillPoints == 0, "the skill point is spent");
            Reject(409, "a second skill point", () => realm.SpendSkill(tokens[0], characters[0], new(101)));
            Progression.Validate(Tables, SkillTable.Preview, spent.Progress);
        }
        string settled = JsonSerializer.Serialize(Read(file).Characters.Select(c => c.Progress));
        using (var realm = new Realm(folder, time, mode))
        {
            var login = realm.Login(new(names[0], Password), false);
            var restored = realm.Characters(login.Token).Single().Progress!;
            Check(JsonSerializer.Serialize(Read(file).Characters.Select(c => c.Progress)) == settled && restored.Level == 2 && restored.SkillLevel(101) == 1, "spent points survive a restart");
            var arena = realm.CreateRoom(login.Token, new(characters[0], "Spent", "")).Id;
            var entity = Actor(realm.Start(login.Token, arena), 1);
            var expected = Progression.Resources(Tables, restored);
            Check(entity.MaxHealth == expected.Life && entity.MaxMana == expected.Mana && expected.Life == 58 && expected.Mana == 18, "vitality and energy raise the next pool");
            realm.Close(login.Token, arena);
        }

        // A document that no rule could have produced is refused instead of served.
        string good = file + ".good"; File.Copy(file, good);
        void Tamper(string reason, Func<Database, Database> edit) { File.Copy(good, file, true); Edit(file, edit); Corrupt(reason, () => new Realm(folder, time, mode).Dispose()); }
        Tamper("an extra attribute point", db => db with { Characters = db.Characters.Select(c => c.Id == characters[0] ? c with { Progress = c.Progress! with { Strength = c.Progress.Strength + 1 } } : c).ToArray() });
        Tamper("unearned stat points", db => db with { Characters = db.Characters.Select(c => c.Id == characters[0] ? c with { Progress = c.Progress! with { StatPoints = 3 } } : c).ToArray() });
        Tamper("unearned skill points", db => db with { Characters = db.Characters.Select(c => c.Id == characters[1] ? c with { Progress = c.Progress! with { SkillPoints = 1 } } : c).ToArray() });
        Tamper("a level without its experience", db => db with { Characters = db.Characters.Select(c => c.Id == characters[1] ? c with { Progress = c.Progress! with { Level = 30 } } : c).ToArray() });
        Tamper("another class's skill", db => db with { Characters = db.Characters.Select(c => c.Id == characters[1] ? c with { Progress = c.Progress! with { Level = 2, Experience = Tables.ExperienceFor(CharacterClass.Sorceress, 2), StatPoints = 5, SkillPoints = 0, Skills = [new(101, 1)] } } : c).ToArray() });
        File.Copy(good, file, true);
        // Room experience needs a started room and a character with a class.
        File.WriteAllBytes(file, playing);
        Edit(file, db => db with { Rooms = [db.Rooms[0] with { Members = db.Rooms[0].Members.Select(m => m.Character == characters[2] ? m with { Experience = 5 } : m).ToArray() }] });
        Corrupt("experience for a character without a class", () => new Realm(folder, time, mode).Dispose());
        File.WriteAllBytes(file, playing);
        Edit(file, db => db with { Rooms = [db.Rooms[0] with { Game = null }] });
        Corrupt("experience in a room that has not started", () => new Realm(folder, time, mode).Dispose());
        File.WriteAllBytes(file, playing);
        using (new Realm(folder, time, mode)) { }
        // The server's own rules decide what is valid: the same document fails under different tables.
        File.Copy(good, file, true);
        var stricter = new ProgressionTables(Enum.GetValues<CharacterClass>().Select(Tables.Get).Select(d => d with { StatPointsPerLevel = 4 }),
            Enum.GetValues<CharacterClass>().ToDictionary(c => c, c => Enumerable.Range(1, 99).Select(l => Tables.ExperienceFor(c, l)).ToArray()), "stricter", false);
        Corrupt("progress written under other tables", () => new Realm(folder, time, mode, stricter).Dispose());
    }

    private static void OpenFiles()
    {
        string folder = Path.Combine(Path.GetTempPath(), "opend2-progress-file-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var skills = SkillTable.Preview;
            var hero = Progression.AddSkill(skills, Progression.AddStat(Progression.Gain(Tables, Progression.Create(Tables, CharacterClass.Barbarian), 20000).Progress, CharacterStat.Strength), 501);
            Progression.Validate(Tables, skills, hero);
            using var realm = new Realm(Path.Combine(folder, "open"), mode: "open");
            var login = realm.Login(new("file-a", Password), true); var peer = realm.Login(new("file-b", Password), true);
            var imported = realm.ImportCharacter(login.Token, new OpenCharacter(4, GameSimulation.RulesVersion, "open", "Barb1", 3, [], [], hero));
            Check(imported.Progress == hero && JsonSerializer.Serialize(imported.Progress) == JsonSerializer.Serialize(hero), "a format 4 file brings its progress");
            var exported = realm.ExportCharacter(login.Token, imported.Id);
            Check(exported.Version == 4 && exported.Progress is not null && JsonSerializer.Serialize(exported.Progress) == JsonSerializer.Serialize(hero), "export writes format 4 with progress");
            string path = Path.Combine(folder, "barb.json"); OpenCharacterFile.Save(path, exported);
            var loaded = OpenCharacterFile.Load(path); Check(loaded.Version == 4 && JsonSerializer.Serialize(loaded.Progress) == JsonSerializer.Serialize(hero), "the file round trips");
            var copy = realm.ImportCharacter(peer.Token, loaded with { Name = "Barb2" });
            Check(copy.Id != imported.Id && copy.Progress == hero, "import to another account is a new character with the same progress");
            var plain = realm.ImportCharacter(peer.Token, new OpenCharacter(3, GameSimulation.RulesVersion, "open", "Plain3", 1, [], []));
            Check(plain.Progress is null && realm.ExportCharacter(peer.Token, plain.Id).Version == 4 && realm.ExportCharacter(peer.Token, plain.Id).Progress is null, "an older file imports without progress and exports as format 4");

            void Refuse(string reason, OpenCharacter file) => Reject(400, reason, () => realm.ImportCharacter(login.Token, file with { Name = "Refused" + Guid.NewGuid().ToString("N")[..6] }));
            Refuse("extra stat points", loaded with { Progress = hero with { StatPoints = hero.StatPoints + 1 } });
            Refuse("unspent points turned into attributes", loaded with { Progress = hero with { Vitality = hero.Vitality + 1 } });
            Refuse("a level the experience does not reach", loaded with { Progress = hero with { Level = hero.Level + 1 } });
            Refuse("a skill of another class", loaded with { Progress = hero with { Skills = [new(101, 1)] } });
            Refuse("skills in the wrong order", loaded with { Progress = hero with { Skills = [new(502, 1), new(501, 1)] } });
            Refuse("an unknown class", loaded with { Progress = hero with { Class = (CharacterClass)40 } });
            Refuse("a level above the cap", loaded with { Progress = hero with { Level = 100 } });
            Refuse("too many skills for the shape bound", loaded with { Progress = hero with { Skills = Enumerable.Range(1, 65).Select(i => new SkillRank(i, 1)).ToArray() } });
            Refuse("progress in a format 3 file", loaded with { Version = 3, Quests = [] });
            Refuse("a null skill list", loaded with { Progress = hero with { Skills = null! } });
            Check(realm.Characters(login.Token).Length == 1, "refused files created nothing");

            // The receiving server's tables decide: a file exported under other rules is refused with an explanation.
            var stricter = new ProgressionTables(Enum.GetValues<CharacterClass>().Select(Tables.Get).Select(d => d with { StatPointsPerLevel = 4 }),
                Enum.GetValues<CharacterClass>().ToDictionary(c => c, c => Enumerable.Range(1, 99).Select(l => Tables.ExperienceFor(c, l)).ToArray()), "stricter", false);
            using var other = new Realm(Path.Combine(folder, "other"), mode: "open", tables: stricter);
            var token = other.Login(new("file-c", Password), true).Token;
            try { other.ImportCharacter(token, loaded); throw new Exception("Character progress: another server's rules accepted the file"); }
            catch (Rejected error) when (error.Status == 400 && error.Message.Contains("this server's rules")) { }

            // Format 4 stays inside the 8 KiB file budget at its bounds: 46 items, 8 quest records and 64 skill ranks.
            var full = OpenItemTransferContracts.FullCharacter();
            var crowded = full with { Version = 4, Quests = Enumerable.Range(0, 8).Select(i => new QuestRecord("cooperative-world-" + i, 999_999)).ToArray(),
                Progress = hero with { Skills = Enumerable.Range(1, 64).Select(i => new SkillRank(i, 20)).ToArray() } };
            string big = Path.Combine(folder, "big.json"); OpenCharacterFile.Save(big, crowded);
            Check(new FileInfo(big).Length <= OpenCharacterFile.MaximumBytes && OpenCharacterFile.Load(big).Progress!.Skills.Length == 64, "the largest format 4 file fits its budget");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        Console.WriteLine("CHARACTER PROGRESS PASS Open format 4 round trip, other-server rules, tampered files and the size budget");
    }
}
