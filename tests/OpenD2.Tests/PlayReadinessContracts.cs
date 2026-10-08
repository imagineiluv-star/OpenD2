using OpenD2.Assets;
using OpenD2.Core;

internal static class PlayReadinessContracts
{
	private static void Check(bool value) { if (!value) throw new Exception("Readiness assertion failed."); }
	public static void Run(Action<string, Action> test)
	{
		test("scene readiness requires every actor's art while keeping GUI and original rules unverified", () =>
		{
			var request = PlaySceneContracts.Request();
			var without = PlaySceneReadiness.Check(LegacyPlayScene.Load(request, PlayAssetContracts.Read));
			Check(without.QuestLoopReachable && !without.ReadyForSceneGuiCheck && without.Issues.Count == 2);
			request = request with { Artwork = [ActorArtContracts.Request(), ActorArtContracts.Request() with { Entity = 2 }] };
			var scene = LegacyPlayScene.Load(request, p => p.EndsWith(".dcc") ? ActorArtContracts.Read(p) : PlayAssetContracts.Read(p));
			var report = PlaySceneReadiness.Check(scene);
			Check(report.ReadyForSceneGuiCheck && report.Issues.Count == 0 && report.GuiQa == "NOT_RUN" && !report.OriginalRulesValidated);
		});
		test("one-way portals cannot pass quest return readiness", () =>
		{
			var request = PlaySceneContracts.Request(); request = request with { Portals = [request.Portals[0]] };
			var report = PlaySceneReadiness.Check(LegacyPlayScene.Load(request, PlayAssetContracts.Read));
			Check(!report.QuestLoopReachable && report.Issues.Contains("quest_return_unreachable:2"));
		});
		test("disconnected regions cannot pass outward quest readiness", () =>
		{
			var request = PlaySceneContracts.Request() with { Portals = [] };
			var report = PlaySceneReadiness.Check(LegacyPlayScene.Load(request, PlayAssetContracts.Read));
			Check(!report.QuestLoopReachable && report.Issues.Contains("quest_target_unreachable:2"));
		});
		test("terrain components detect targets separated by impassable cells in one region", () =>
		{
			var cells = Enumerable.Repeat(CollisionCell.Open, 25).ToArray(); for (int y = 0; y < 5; y++) cells[y * 5 + 2] = CollisionCell.Unknown;
			var world = new WorldDefinition([new("Split", new(new(1), 5, 5, cells))], [], new(new(10), new(1), new(384, 640), "Guide"), [new(2)], "Test");
			EntityState[] actors = [new(new(1), new(1), new(384, 384)), new(new(2), new(1), new(896, 896), Kind: EntityKind.Monster)];
			var issues = new List<string>(); Check(!PlaySceneReadiness.CheckQuestLoop(world, actors, issues));
			Check(issues.Contains("quest_target_unreachable:2"));
		});
	}
}
