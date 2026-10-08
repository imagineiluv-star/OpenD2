using OpenD2.Assets;
using System.Text.Json;

internal static class ArtworkSetupContracts
{
	private static void Check(bool value) { if (!value) throw new Exception("Artwork setup assertion failed."); }
	private static void Bad(Action action)
	{ try { action(); } catch (Exception e) when (e is ArgumentException or InvalidDataException) { return; } throw new Exception("Expected invalid artwork form."); }
	private static LegacyMotionRequest Motion(string name = "Idle", string path = "actor.dcc", string layers = "", string directions = "0,0,0,0,1,1,1,1", double fps = 12.34567) => LegacyArtworkSetup.ParseMotion(name, path, layers, directions, fps);
	private static LegacyActorRequest Actor(uint id, bool cof = false) => new(id, PlayAssetContracts.Request.PalettePath,
		Enum.GetNames<ActorMotion>().Select(m => Motion(m, cof ? "actor.cof" : "actor.dcc", cof ? "0=a.dcc\n1=b.dcc" : "")).ToArray());
	private static byte[] Read(string path) => path.EndsWith(".dcc") || path.EndsWith(".cof") ? ActorArtContracts.Read(path) : PlayAssetContracts.Read(path);
	public static void Run(string root, Action<string, Action> test)
	{
		test("artwork form DCC and COF parse into loader-compatible five-motion profiles", () =>
		{
			foreach (bool cof in new[] { false, true })
			{
				var art = LegacyActorArt.Load(Actor(1, cof), Read);
				Check(art.Motions.Count == 5 && art.Motions[ActorMotion.Walk].Fps == 12.34567 && art.Motions[ActorMotion.Attack].Directions.Count == 8);
			}
			var layers = LegacyArtworkSetup.ParseLayers(" 0 = DATA/Head.DCC\r\n 1 = torso.dcc \n");
			Check(layers[0] == "data\\head.dcc" && layers[1] == "torso.dcc");
		});
		test("artwork form rejects invalid directions without inferring missing facings", () =>
		{
			foreach (string value in new[] { "", "0,1", "0,0,0,0,0,0,0,", "0,0,0,0,0,0,0,-1", "0,0,0,0,0,0,0,32", "0,0,0,0,0,0,0,1.5", new string('0', 129) }) Bad(() => Motion(directions: value));
			Check(Motion(directions: "0, 1, 2, 3, 4, 5, 6, 31").Directions.SequenceEqual(new[] { 0, 1, 2, 3, 4, 5, 6, 31 }));
		});
		test("artwork form rejects unsafe, duplicate and incompatible component paths", () =>
		{
			foreach (string value in new[] { "0=a.dcc\n0=b.dcc", "16=a.dcc", "0=../a.dcc", "0=a.txt", "a.dcc", new string(' ', 32769) }) Bad(() => LegacyArtworkSetup.ParseLayers(value));
			Bad(() => Motion(path: "a.cof")); Bad(() => Motion(layers: "0=a.dcc")); Bad(() => Motion(path: "../actor.dcc")); Bad(() => Motion(path: "a.dc6"));
			foreach (double fps in new[] { 0d, -1, 121, double.NaN, double.PositiveInfinity }) Bad(() => Motion(fps: fps));
			Bad(() => Motion(name: "0")); Bad(() => Motion(name: "Run"));
		});
		test("saving artwork copies preserves terrain, placements, portals and source while changing content identity", () =>
		{
			var request = PlaySceneContracts.Request(); string original = Path.Combine(root, "art-original.json"), copy = Path.Combine(root, "art-copy.json");
			LegacySceneSetup.SaveNew(original, request, Read); byte[] before = File.ReadAllBytes(original);
			var next = LegacySceneRequest.Read(original) with { Artwork = [Actor(1), Actor(2, true)] };
			var ready = LegacySceneSetup.SaveNew(copy, next, Read); var restored = LegacySceneRequest.Read(copy);
			Check(JsonSerializer.Serialize(restored with { Artwork = null }) == JsonSerializer.Serialize(request));
			Check(File.ReadAllBytes(original).SequenceEqual(before) && ready.ReadyForSceneGuiCheck && !ready.OriginalRulesValidated && ready.GuiQa == "NOT_RUN");
			Check(ready.ContentId != LegacyPlayScene.Load(request, Read).ContentId);
			Check(LegacyPlayScene.Load(restored, Read).Artwork.Count == 2);
		});
		test("invalid source direction or missing COF layer prevents artwork copy creation", () =>
		{
			string folder = Path.Combine(root, "art-invalid"); var actor = Actor(1); actor.Motions[0] = Motion(directions: "31,31,31,31,31,31,31,31");
			Bad(() => LegacySceneSetup.SaveNew(Path.Combine(folder, "directions.json"), PlaySceneContracts.Request() with { Artwork = [actor] }, Read));
			actor = Actor(1, true); actor.Motions[0].Layers!.Remove(1);
			Bad(() => LegacySceneSetup.SaveNew(Path.Combine(folder, "layers.json"), PlaySceneContracts.Request() with { Artwork = [actor] }, Read));
			Check(!Directory.Exists(folder));
		});
		test("partial artwork copies keep other actors explicitly unready for original visual acceptance", () =>
		{
			var request = PlaySceneContracts.Request() with { Artwork = [Actor(1)] };
			var ready = LegacySceneSetup.SaveNew(Path.Combine(root, "art-partial.json"), request, Read);
			Check(ready.ActorsWithArtwork == 1 && !ready.ReadyForSceneGuiCheck && ready.Issues.Contains("actor_artwork_missing:2"));
		});
	}
}
