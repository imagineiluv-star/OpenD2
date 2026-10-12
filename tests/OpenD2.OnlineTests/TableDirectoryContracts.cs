using System.Text.Json;
using OpenD2.Assets;
using OpenD2.Core;
using OpenD2.Server;

// --tables: the server reads the owner's character tables from a directory of plain TXT files. The files here are synthetic, so these
// contracts prove the directory handling, the data-directory guard and what a server on other tables does differently; they say
// nothing about Diablo II's own numbers.
internal static class TableDirectoryContracts
{
    private const string Password = "table-directory-contract-password";
    private static void Check(bool value, string reason) { if (!value) throw new Exception("Table directory: " + reason); }
    private static void Throws<T>(string reason, Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception($"Table directory: expected {typeof(T).Name} for {reason}");
    }
    private static string Temp() { string folder = Path.Combine(Path.GetTempPath(), "opend2-tables-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder); return folder; }

    internal static void Run()
    {
        Reading();
        foreach (string mode in new[] { "realm", "open" }) Server(mode);
        Console.WriteLine("TABLE DIRECTORY PASS flat and install layouts, errors, data-directory guard and a server on other tables");
    }

    private static void Reading()
    {
        string flat = Temp(), install = Temp(), empty = Temp();
        try
        {
            TableFixtures.Write(flat); TableFixtures.Write(install, installLayout: true);
            var a = TableDirectory.Load(flat); var b = TableDirectory.Load(install);
            Check(a.Progression.Original && a.Skills.All.Count == SkillTable.Preview.All.Count && a.Progression.Get(CharacterClass.Amazon).Strength == ProgressionTables.Preview.Get(CharacterClass.Amazon).Strength + 1, "the tables are read");
            Check(a.Progression.Source == b.Progression.Source && a.Progression.Source.StartsWith("lod-1.10f; charstats "), "the same bytes give the same label in either layout, whatever the file names' case");
            Check(a.Sources.Count == 3 && a.Sources.All(s => s.Sha256.Length == 64), "every file has a recorded hash");
            Check(TableDirectory.Load(flat).Progression.Source == a.Progression.Source, "loading is repeatable");

            Throws<DirectoryNotFoundException>("a directory that is not there", () => TableDirectory.Load(Path.Combine(flat, "missing")));
            Throws<ArgumentException>("an empty path", () => TableDirectory.Load(" "));
            var missing = Assert<FileNotFoundException>("a directory without the tables", () => TableDirectory.Load(empty));
            Check(missing.Message.Contains("not found") && (missing.Message.Contains("charstats") || missing.Message.Contains("experience") || missing.Message.Contains("skills")), "the message names the missing file");
            File.Delete(Path.Combine(flat, "skills.txt"));
            Check(Assert<FileNotFoundException>("one table missing", () => TableDirectory.Load(flat)).Message.Contains("skills.txt"), "a single missing file is named");
            File.WriteAllText(Path.Combine(flat, "skills.txt"), TableFixtures.Skills().Replace("\tama\t", "\txxx\t"));
            Check(Assert<InvalidDataException>("a malformed table", () => TableDirectory.Load(flat)).Message.Contains("skills.txt"), "a malformed table names its file and line");
            File.WriteAllBytes(Path.Combine(flat, "skills.txt"), new byte[ExcelTextTable.MaxBytes + 1]);
            Throws<InvalidDataException>("an oversized table", () => TableDirectory.Load(flat));
        }
        finally { foreach (var folder in new[] { flat, install, empty }) if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    private static T Assert<T>(string reason, Action action) where T : Exception
    {
        try { action(); } catch (T error) { return error; }
        throw new Exception($"Table directory: expected {typeof(T).Name} for {reason}");
    }

    private static void Server(string mode)
    {
        string tables = Temp(), data = Temp(), other = Temp(), plain = Temp();
        try
        {
            TableFixtures.Write(tables);
            var loaded = TableDirectory.Load(tables);
            string file = Path.Combine(data, "realm.json"); var time = new TestTime();
            Guid hero;
            using (var realm = new Realm(data, time, mode, loaded.Progression, loaded.Skills))
            {
                Check(realm.TablesAreOriginal, "a server on the owner's tables says so");
                var token = realm.Login(new("table-user", Password), true).Token;
                var made = realm.CreateCharacter(token, "OwnTables", "Amazon"); hero = made.Id;
                Check(made.Progress!.Strength == ProgressionTables.Preview.Get(CharacterClass.Amazon).Strength + 1, "a new character starts from the owner's table, not the preview");
                Check(realm.Skills(token).Length == loaded.Skills.All.Count, "the skill list is the owner's table");
            }
            using (var realm = new Realm(Path.Combine(plain, "d"), time, mode)) Check(!realm.TablesAreOriginal, "the default server is on the preview");
            var written = JsonSerializer.Deserialize<Database>(File.ReadAllBytes(file))!;
            Check(written.Tables == loaded.Progression.Source, "the data directory records the table set it was built with");

            // The same files again (a restart) are the same table set.
            using (var again = new Realm(data, time, mode, TableDirectory.Load(tables).Progression, loaded.Skills))
                Check(again.Characters(again.Login(new("table-user", Password), false).Token).Single().Id == hero, "a restart on the same tables keeps the character");
            // Characters built on other tables are refused, in either direction, instead of being served or silently reinterpreted.
            var preview = Assert<InvalidDataException>("owner's characters on the preview", () => new Realm(data, time, mode).Dispose());
            Check(preview.Message.Contains("one data directory per table set"), "the refusal says what to do");
            TableFixtures.Write(other, strengthBonus: 2);
            var changed = TableDirectory.Load(other);
            Throws<InvalidDataException>("owner's characters on a changed table", () => new Realm(data, time, mode, changed.Progression, changed.Skills).Dispose());
            using (var previewRealm = new Realm(Path.Combine(other, "preview"), time, mode))
            {
                var token = previewRealm.Login(new("preview-user", Password), true).Token;
                previewRealm.CreateCharacter(token, "PreviewHero", "Druid");
            }
            Throws<InvalidDataException>("preview characters on the owner's tables", () => new Realm(Path.Combine(other, "preview"), time, mode, loaded.Progression, loaded.Skills).Dispose());
            // A directory with accounts only has nothing that depends on the tables, so it can move to another table set.
            using (var accounts = new Realm(Path.Combine(plain, "accounts"), time, mode)) accounts.Login(new("only-account", Password), true);
            using (var moved = new Realm(Path.Combine(plain, "accounts"), time, mode, loaded.Progression, loaded.Skills))
            {
                moved.Login(new("only-account", Password), false);
                moved.Login(new("second-account", Password), true);
            }
            Check(JsonSerializer.Deserialize<Database>(File.ReadAllBytes(Path.Combine(plain, "accounts", "realm.json")))!.Tables == loaded.Progression.Source, "the moved directory is now tied to the new table set");
        }
        finally { foreach (var folder in new[] { tables, data, other, plain }) if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
