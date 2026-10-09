using System.Security.Cryptography;
using System.Text;
using OpenD2.Assets;
using OpenD2.Core;

internal static class ItemTableContracts
{
	internal static LegacyItemDefinitionsRequest Request() => new(ItemTables.Profile, [new("TrainingSword", "fws"), new("TrainingVest", "far")]);
	internal static byte[] Read(string path) => Encoding.ASCII.GetBytes(path.Split('\\', '/')[^1] switch
	{
		"bodylocs.txt" => "Name\tCode\nNone\tnone\nRight hand\trarm\nLeft hand\tlarm\nTorso\ttors\n",
		"itemtypes.txt" => "ItemType\tCode\tEquiv1\tEquiv2\tBody\tBodyLoc1\tBodyLoc2\nWeapon\tweap\t\t\t1\trarm\tlarm\nChild\tchld\tweap\t\t1\trarm\tlarm\nArmor\tarmo\t\t\t1\ttors\t\nMisc\tmisc\t\t\t0\t\t\n",
		"weapons.txt" => "name\tcode\tnamestr\ttype\ttype2\tinvwidth\tinvheight\tinvfile\tlevelreq\tstackable\tmindam\tmaxdam\t2handmindam\t2handmaxdam\tminmisdam\tmaxmisdam\treqstr\treqdex\nFixture sword\tfws\tFixtureSword\tchld\tweap\t1\t3\tfixtureweapon\t2\t0\t7\t13\t14\t26\t1\t4\t12\t8\nExpansion\n\n",
		"armor.txt" => "name\tcode\tnamestr\ttype\ttype2\tinvwidth\tinvheight\tinvfile\tlevelreq\tstackable\tminac\tmaxac\treqstr\tmindam\tmindam\nFixture vest\tfar\tFixtureVest\tarmo\t\t2\t3\tfixturearmor\t1\t0\t3\t8\t10\t\t\n",
		"misc.txt" => "name\tcode\tnamestr\ttype\ttype2\tinvwidth\tinvheight\tinvfile\tlevelreq\tstackable\nFixture misc\tfms\tFixtureMisc\tmisc\t\t1\t1\tfixturemisc\t0\t1\n",
		_ => throw new InvalidDataException("Unexpected test table: " + path)
	});
	private static byte[] SceneRead(string path) => path.EndsWith(".txt") ? Read(path) : path.EndsWith(".dcc") ? ActorArtContracts.Read(path) : PlayAssetContracts.Read(path);
	private static void Check(bool value) { if (!value) throw new Exception("Item table assertion failed."); }
	private static void Bad(Action action)
	{ try { action(); } catch (Exception e) when (e is ArgumentException or InvalidDataException) { return; } throw new Exception("Expected invalid item definitions."); }
	private static byte[] Replace(string path, string file, string from, string to) => path.EndsWith(file) ? Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(Read(path)).Replace(from, to)) : Read(path);
	private static byte[] Padded(string path)
	{ var source = Read(path); var bytes = new byte[ExcelTextTable.MaxBytes]; bytes.AsSpan().Fill(10); source.CopyTo(bytes, 0); return bytes; }
	public static void Run(string root, Action<string, Action> test)
	{
		test("item TXT definitions preserve codes, slots, independent damage ranges, dimensions and source provenance", () =>
		{
			var tables = ItemTables.Load(Read); var sword = tables.Get("fws"); var armor = tables.Get("far"); var misc = tables.Get("fms");
			Check(tables.Items.Count == 3 && tables.Sources.Count == 5 && sword.Line == 2);
			Check(sword.NameKey == "FixtureSword" && sword.Type == "chld" && sword.Type2 == "weap" && sword.Width == 1 && sword.Height == 3);
			Check(sword.Equippable && sword.BodyLoc1 == "rarm" && sword.BodyLoc2 == "larm" && sword.InventoryPath == "data\\global\\items\\fixtureweapon.dc6");
			Check(sword.Stats == new ItemBaseStats(7, 13, 14, 26, 1, 4, 0, 0, 12, 8, 2));
			Check(armor.Kind == ItemTableKind.Armor && armor.Width == 2 && armor.Stats.MinimumDefense == 3 && armor.Stats.MaximumDefense == 8 && armor.Stats.RequiredDexterity == 0);
			Check(misc.Stackable && !misc.Equippable && misc.Stats.MinimumDamage == 0);
			foreach (var source in tables.Sources) Check(source.Bytes == Read(source.Path).Length && source.Sha256 == Convert.ToHexStringLower(SHA256.HashData(Read(source.Path))));
		});
		test("item TXT profile and binding shape reject unsupported input before I/O", () =>
		{
			byte[] NoRead(string _) => throw new Exception("Invalid request reached I/O.");
			Bad(() => ItemTables.Load(NoRead, "d2r")); var good = Request();
			foreach (var bad in new[] { good with { Profile = "lod-1.13" }, good with { Bindings = null! }, good with { Bindings = [] }, good with { Bindings = [null!] },
				good with { Bindings = [good.Bindings[0], good.Bindings[0]] }, good with { Bindings = [new("0", "fws")] }, good with { Bindings = [new("Unknown", "fws")] },
				good with { Bindings = [new("TrainingSword", null!)] }, good with { Bindings = [new("TrainingSword", "../a")] }, good with { Bindings = [new("TrainingSword", "abcde")] } })
				Bad(() => LegacyItemDefinitions.Load(bad, NoRead));
		});
		test("item tables reject missing and ambiguous used columns while ignoring unrelated duplicate columns", () =>
		{
			Bad(() => ItemTables.Load(p => Replace(p, "weapons.txt", "\tinvwidth\t", "\tmissing\t")));
			Bad(() => ItemTables.Load(p => Replace(p, "weapons.txt", "\tinvheight\t", "\tINVWIDTH\t")));
			Check(ItemTables.Load(Read).Get("far").Code == "far"); // armor fixture has two unrelated mindam columns.
		});
		test("item definitions reject missing type, equivalence and body references and type cycles", () =>
		{
			Bad(() => ItemTables.Load(p => Replace(p, "weapons.txt", "\tchld\tweap\t", "\tnope\tweap\t")));
			Bad(() => ItemTables.Load(p => Replace(p, "weapons.txt", "\tchld\tweap\t", "\tchld\tnope\t")));
			Bad(() => ItemTables.Load(p => Replace(p, "itemtypes.txt", "\tchld\tweap\t", "\tchld\tnope\t")));
			Bad(() => ItemTables.Load(p => Replace(p, "itemtypes.txt", "\tweap\t\t\t1", "\tweap\tchld\t\t1")));
			Bad(() => ItemTables.Load(p => Replace(p, "itemtypes.txt", "\trarm\tlarm", "\tnope\tlarm")));
			string chain = string.Join('\n', Enumerable.Range(0, 66).Select(i => $"Type\tt{i:000}\t{(i == 0 ? "" : $"t{i - 1:000}")}\t\t0\t\t")) + "\n";
			Bad(() => ItemTables.Load(p => p.EndsWith("itemtypes.txt") ? Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(Read(p)) + chain) : Read(p)));
		});
		test("item tables reject duplicate keys within or across tables", () =>
		{
			Bad(() => ItemTables.Load(p => Replace(p, "misc.txt", "\tfms\t", "\tfws\t")));
			Bad(() => ItemTables.Load(p => Replace(p, "bodylocs.txt", "\ttors\n", "\trarm\n")));
			Bad(() => ItemTables.Load(p => Replace(p, "itemtypes.txt", "\tchld\t", "\tweap\t")));
		});
		test("item definitions reject unsafe image paths, invalid dimensions and numeric ranges", () =>
		{
			foreach (var file in new[] { "../bad", "sub/path", "bad.dc6", "", "bad name" }) Bad(() => ItemTables.Load(p => Replace(p, "weapons.txt", "fixtureweapon", file)));
			foreach (var width in new[] { "0", "11", "-1", "+1", "999999999999" }) Bad(() => ItemTables.Load(p => Replace(p, "weapons.txt", "\t1\t3\tfixtureweapon", "\t" + width + "\t3\tfixtureweapon")));
			Bad(() => ItemTables.Load(p => Replace(p, "weapons.txt", "\t7\t13\t", "\t14\t13\t")));
			Bad(() => ItemTables.Load(p => Replace(p, "weapons.txt", "\t7\t13\t", "\t7\t256\t")));
			Bad(() => ItemTables.Load(p => Replace(p, "armor.txt", "\t3\t8\t10", "\t9\t8\t10")));
		});
		test("item tables enforce byte and retained item-count budgets", () =>
		{
			Bad(() => ItemTables.Load(_ => new byte[ExcelTextTable.MaxBytes + 1]));
			try { ItemTables.Load(Padded); throw new Exception("Expected aggregate budget rejection."); }
			catch (InvalidDataException error) when (error.Message.Contains("input budget")) { }
			var lines = Encoding.ASCII.GetString(Read("misc.txt")).Split('\n');
			var many = Encoding.ASCII.GetBytes(lines[0] + "\n" + string.Join('\n', Enumerable.Range(0, ItemTables.MaxItems).Select(i => lines[1].Replace("\tfms\t", "\t" + i.ToString("x4") + "\t"))));
			Bad(() => ItemTables.Load(p => p.EndsWith("misc.txt") ? many : Read(p)));
		});
		test("definition bindings resolve existing compatible items and reject wrong categories", () =>
		{
			var loaded = LegacyItemDefinitions.Load(Request(), Read); Check(loaded.Bindings.Count == 2 && loaded.Bindings[ItemDefinition.TrainingSword].Code == "fws");
			foreach (var binding in new[] { new LegacyItemBinding("TrainingSword", "nope"), new("TrainingSword", "FWS"), new("TrainingSword", "far"), new("TrainingVest", "fws"), new("TrainingSword", "fms") })
				Bad(() => LegacyItemDefinitions.Load(new(ItemTables.Profile, [binding]), Read));
		});
		test("definition settings and all table bytes affect scene identity with owned request snapshot", () =>
		{
			var request = PlaySceneContracts.Request() with { ItemDefinitions = Request() }; var before = LegacyPlayScene.Load(request, SceneRead);
			Check(before.ContentId != LegacyPlayScene.Load(request with { ItemDefinitions = request.ItemDefinitions with { Bindings = [request.ItemDefinitions.Bindings[0]] } }, SceneRead).ContentId);
			Check(before.ContentId != LegacyPlayScene.Load(request, p => p.EndsWith("misc.txt") ? Replace(p, "misc.txt", "Fixture misc", "Changed misc") : SceneRead(p)).ContentId);
			var snapshot = LegacyPlayScene.Load(request, p => { request.ItemDefinitions.Bindings[0] = new("TrainingSword", "nope"); return SceneRead(p); });
			Check(snapshot.ContentId == before.ContentId && snapshot.ItemDefinitions!.Bindings[ItemDefinition.TrainingSword].Code == "fws");
		});
		test("optional reference definitions preserve existing scene hashes, authoritative simulation and saved gameplay", () =>
		{
			var request = PlaySceneContracts.Request(); var before = LegacyPlayScene.Load(request, SceneRead); var after = LegacyPlayScene.Load(request with { ItemDefinitions = Request() }, SceneRead);
			Check(before.ContentId == LegacyPlayScene.Load(request with { ItemDefinitions = null }, SceneRead).ContentId && before.World.ContentHash == after.World.ContentHash);
			var a = before.Create(3); var b = after.Create(3); for (int i = 0; i < 60; i++) { a.Step(); b.Step(); Check(a.ComputeStateHash() == b.ComputeStateHash()); }
			string file = Path.Combine(root, "item-reference-game.json"); GameSave.Save(file, a.CaptureSnapshot());
			Check(GameSave.Load(file, world: after.World).Simulation.ComputeStateHash() == a.ComputeStateHash());
			var ready = PlaySceneReadiness.Check(after); Check(ready.ItemDefinitionCount == 2 && !ready.OriginalRulesValidated && ready.GuiQa == "NOT_RUN");
		});
		test("definition scene save round trips, rejects invalid bindings and preserves existing files", () =>
		{
			var request = PlaySceneContracts.Request() with { ItemDefinitions = Request() }; string file = Path.Combine(root, "item-reference-scene.json");
			var ready = LegacySceneSetup.SaveNew(file, request, SceneRead); byte[] before = File.ReadAllBytes(file);
			Check(ready.ItemDefinitionCount == 2 && LegacyPlayScene.Load(LegacySceneRequest.Read(file), SceneRead).ContentId == ready.ContentId);
			string invalid = Path.Combine(root, "invalid-item-reference.json");
			Bad(() => LegacySceneSetup.SaveNew(invalid, request with { ItemDefinitions = new(ItemTables.Profile, [new("TrainingSword", "far")]) }, SceneRead));
			Check(!File.Exists(invalid) && File.ReadAllBytes(file).SequenceEqual(before));
		});
		test("item tables participate in the combined scene input budget", () =>
		{
			var request = PlaySceneContracts.Request() with { Artwork = [ActorArtContracts.Request(), ActorArtContracts.Request() with { Entity = 2 }], ItemDefinitions = Request() };
			var actor = NpcArtContracts.LargeDcc(10 * 1024 * 1024);
			byte[] Load(string p) => p.EndsWith(".txt") ? Padded(p) : p.EndsWith(".dcc") ? actor : SceneRead(p);
			Check(LegacyPlayScene.Load(request with { ItemDefinitions = null }, Load).Artwork.Count == 2);
			try { LegacyPlayScene.Load(request, Load); throw new Exception("Expected scene budget rejection."); }
			catch (InvalidDataException error) when (error.Message.Contains("Scene input exceeds")) { }
		});
	}
}
