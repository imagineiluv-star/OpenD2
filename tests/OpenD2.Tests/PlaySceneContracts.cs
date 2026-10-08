using OpenD2.Assets;
using OpenD2.Core;

internal static class PlaySceneContracts
{
	internal static LegacySceneRequest Request() => new(1, "Clear the test region",
		[new(1, "Town", PlayAssetContracts.Request with { Tilesets = PlayAssetContracts.Request.Tilesets.ToArray() }), new(2, "Dungeon", PlayAssetContracts.Request with { Tilesets = PlayAssetContracts.Request.Tilesets.ToArray() })],
		[new(1, 1, 384, 384, true), new(2, 2, 1664, 640, false, 36)],
		new(10, 1, 640, 384, "Guide"), [new(11, 1, 384, 384, 2, 384, 384), new(12, 2, 384, 384, 1, 384, 384)], [2]);
	private static void Check(bool value) { if (!value) throw new Exception("Play scene assertion failed."); }
	private static void Bad(Action action) { try { action(); } catch (Exception e) when (e is ArgumentException or InvalidDataException) { return; } throw new Exception("Expected invalid scene."); }
	public static void Run(string root, Action<string, Action> test)
	{
		test("legacy terrain connects to world, portal, collision, save and replay", () =>
		{
			var content = LegacyPlayScene.Load(Request(), PlayAssetContracts.Read); var simulation = content.Create(1);
			Check(simulation.Collision!.Width == 10 && content.Terrain.Count == 2);
			Check(simulation.Submit(new(1, 1, new(1), new(1), CommandKind.Interact, Target: new(11))) == CommandResult.Accepted);
			simulation.Step(); Check(simulation.ActiveRegion == new RegionId(2));
			string save = Path.Combine(root, "legacy-play.json"); GameSave.Save(save, simulation.CaptureSnapshot());
			var restored = GameSave.Load(save, world: content.World).Simulation;
			Check(restored.ComputeStateHash() == simulation.ComputeStateHash());
			for (int i = 0; i < 20; i++) { simulation.Step(); restored.Step(); }
			Check(restored.ComputeStateHash() == simulation.ComputeStateHash());
		});
		test("scene preflight rejects invalid spawns, quest ownership and missing region", () =>
		{
			var good = Request();
			foreach (var bad in new[] { good with { Actors = [new(1, 1, 0, 0, true), good.Actors[1]] }, good with { QuestTargets = [1] },
				good with { Portals = [new(11, 1, 384, 384, 3, 384, 384)] }, good with { Actors = [new(2, 1, 384, 384, true), good.Actors[1]] },
				good with { Regions = [good.Regions[0], good.Regions[0]] }, good with { Regions = null! } })
				Bad(() => LegacyPlayScene.Load(bad, PlayAssetContracts.Read));
		});
		test("legacy content identity changes with terrain bytes or spawn values", () =>
		{
			var good = Request(); var first = LegacyPlayScene.Load(good, PlayAssetContracts.Read);
			var changed = LegacyPlayScene.Load(good, p => { var bytes = PlayAssetContracts.Read(p); if (p.EndsWith("pal.dat")) bytes[0] = 12; return bytes; });
			Check(first.ContentId != changed.ContentId && first.World.ContentHash == changed.World.ContentHash);
			var health = LegacyPlayScene.Load(good with { Actors = [good.Actors[0], good.Actors[1] with { Health = 40 }] }, PlayAssetContracts.Read);
			Check(first.ContentId != health.ContentId);
			good.Actors[0] = good.Actors[0] with { X = 0 }; good.Regions[0].Terrain.Tilesets[0] = "changed.dt1";
			Check(first.Create(1).GetEntity(new(1)).Position == new GamePosition(384, 384));
			good.Regions[0].Terrain.Tilesets[0] = "data/global/tiles/test.dt1";
		});
		test("legacy projection matches tile/subtile geometry and round trips", () =>
		{
			Check(LegacyProjection.Project(1280, 0) == (80d, 40d));
			Check(LegacyProjection.Project(0, 1280) == (-80d, 40d));
			foreach (var p in new[] { new GamePosition(384, 640), new(-512, 128), new(0, 0), new(GameSimulation.PositionLimit, 0) })
			{ var screen = LegacyProjection.Project(p.X, p.Y); Check(LegacyProjection.Unproject(screen.X, screen.Y) == p); }
			Bad(() => LegacyProjection.Unproject(double.NaN, 0)); Bad(() => LegacyProjection.Unproject(1e20, 0));
		});
	}
}
