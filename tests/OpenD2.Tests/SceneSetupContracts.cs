using OpenD2.Assets;
using OpenD2.Core;

internal static class SceneSetupContracts
{
	private static LegacySceneRequest Request() => LegacySceneSetup.Create(PlayAssetContracts.Request, "Setup test", 1, 1, 6, 2, 2, 1);
	private static void Check(bool value) { if (!value) throw new Exception("Scene setup assertion failed."); }
	private static void Bad(Action action)
	{ try { action(); } catch (Exception e) when (e is ArgumentException or InvalidDataException) { return; } throw new Exception("Expected invalid scene setup."); }
	public static void Run(string root, Action<string, Action> test)
	{
		test("scene setup reads synthetic MPQ resources through the production installation loader without modifying them", () =>
		{
			string folder = Path.Combine(root, "setup-mpq"); Directory.CreateDirectory(folder);
			var map = PlayAssetContracts.Request;
			var entries = new[] { ("d2data.mpq", map.MapPath), ("d2exp.mpq", map.Tilesets[0]), ("patch_d2.mpq", map.PalettePath) };
			foreach (var (archive, logical) in entries)
			{
				byte[] bytes = PlayAssetContracts.Read(logical);
				Check(MpqContracts.Fixture(Path.Combine(folder, archive), MpqArchive.NormalizePath(logical), bytes, (uint)bytes.Length, 1) == 0);
			}
			var before = entries.Select(e => File.ReadAllBytes(Path.Combine(folder, e.Item1))).ToArray();
			string file = Path.Combine(root, "setup-from-mpq.json");
			var report = LegacySceneSetup.SaveNew(file, Request(), path => AssetDecoders.ReadFromInstall(folder, path));
			Check(LegacyPlayScene.Load(folder, LegacySceneRequest.Read(file)).ContentId == report.ContentId);
			for (int i = 0; i < entries.Length; i++) Check(File.ReadAllBytes(Path.Combine(folder, entries[i].Item1)).SequenceEqual(before[i]));
		});
		test("scene setup converts cells and round trips into a playable preview without original art claims", () =>
		{
			string file = Path.Combine(root, "setup", "scene.json");
			var ready = LegacySceneSetup.SaveNew(file, Request(), PlayAssetContracts.Read);
			var scene = LegacyPlayScene.Load(LegacySceneRequest.Read(file), PlayAssetContracts.Read);
			Check(scene.Create(1).GetEntity(new(1)).Position == new GamePosition(384, 384));
			Check(scene.Actors[1].Position == new GamePosition(1664, 640) && scene.World.QuestGiver.Position == new GamePosition(640, 384));
			Check(ready.ContentId == scene.ContentId && ready.QuestLoopReachable && !ready.ReadyForSceneGuiCheck && !ready.OriginalRulesValidated && ready.GuiQa == "NOT_RUN" && ready.ActorsWithArtwork == 0);
		});
		test("scene setup rejects overlap, out of range coordinates, unsafe paths and invalid titles", () =>
		{
			foreach (int x in new[] { -1, 4096, int.MaxValue }) Bad(() => LegacySceneSetup.Create(PlayAssetContracts.Request, "Test", x, 1, 6, 2, 2, 1));
			Bad(() => LegacySceneSetup.Create(PlayAssetContracts.Request, "Test", 1, 1, 1, 1, 2, 1));
			Bad(() => LegacySceneSetup.Create(PlayAssetContracts.Request with { MapPath = "../bad.ds1" }, "Test", 1, 1, 6, 2, 2, 1));
			foreach (string title in new[] { "", "bad\nname", new string('x', 81) }) Bad(() => LegacySceneSetup.Create(PlayAssetContracts.Request, title, 1, 1, 6, 2, 2, 1));
		});
		test("scene setup validates bounds and quest connectivity before creating output", () =>
		{
			string file = Path.Combine(root, "setup-invalid", "scene.json");
			Bad(() => LegacySceneSetup.SaveNew(file, LegacySceneSetup.Create(PlayAssetContracts.Request, "Test", 30, 1, 6, 2, 2, 1), PlayAssetContracts.Read));
			Bad(() => LegacySceneSetup.SaveNew(file, PlaySceneContracts.Request() with { Portals = [] }, PlayAssetContracts.Read));
			Check(!Directory.Exists(Path.GetDirectoryName(file)));
		});
		test("scene setup cannot overwrite a previous file and removes temporary output on failure", () =>
		{
			string file = Path.Combine(root, "setup-existing.json"); File.WriteAllText(file, "keep me");
			try { LegacySceneSetup.SaveNew(file, Request(), PlayAssetContracts.Read); throw new Exception("Expected existing file failure."); }
			catch (IOException) { }
			Check(File.ReadAllText(file) == "keep me" && !Directory.EnumerateFiles(root, "setup-existing.json.*.tmp").Any());
		});
		test("scene setup writes the validated snapshot despite reader mutation", () =>
		{
			string file = Path.Combine(root, "setup-snapshot.json"); var request = Request();
			var result = LegacySceneSetup.SaveNew(file, request, path =>
			{
				request.Regions[0].Terrain.Tilesets[0] = "changed.dt1"; request.Actors[0] = request.Actors[0] with { X = 0 };
				return PlayAssetContracts.Read(path);
			});
			var loaded = LegacyPlayScene.Load(LegacySceneRequest.Read(file), PlayAssetContracts.Read);
			Check(loaded.ContentId == result.ContentId && loaded.Actors[0].Position.X == 384);
		});
		test("scene setup missing resources leave no new scene or temporary files", () =>
		{
			string folder = Path.Combine(root, "setup-missing");
			try { LegacySceneSetup.SaveNew(Path.Combine(folder, "scene.json"), Request(), _ => throw new FileNotFoundException("absent")); throw new Exception("Expected missing source failure."); }
			catch (FileNotFoundException) { }
			Check(!Directory.Exists(folder));
		});
	}
}
