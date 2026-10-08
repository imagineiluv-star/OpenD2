using OpenD2.Assets;
using OpenD2.Core;

internal static class ActorArtContracts
{
	internal static LegacyActorRequest Request() => new(1, "data/global/palette/act1/pal.dat",
		Enum.GetNames<ActorMotion>().Select(m => new LegacyMotionRequest(m, "actor.dcc", null, [0, 0, 0, 0, 1, 1, 1, 1], 10)).ToArray());
	internal static byte[] Read(string path) => path.EndsWith(".dcc") ? AnimationContracts.Dcc() : path.EndsWith(".cof") ? AnimationContracts.Cof() : new byte[768];
	private static void Check(bool value) { if (!value) throw new Exception("Actor art assertion failed."); }
	private static void Bad(Action action) { try { action(); } catch (Exception e) when (e is ArgumentException or InvalidDataException) { return; } throw new Exception("Expected invalid artwork."); }
	public static void Run(Action<string, Action> test)
	{
		test("actor art loads all motions and shares repeated direction clips", () =>
		{
			var art = LegacyActorArt.Load(Request(), Read); Check(art.Motions.Count == 5 && art.PixelCount > 0);
			var idle = art.Motions[ActorMotion.Idle];
			Check(idle.Directions.Count == 8 && ReferenceEquals(idle.Directions[0], idle.Directions[1]) && !ReferenceEquals(idle.Directions[0], idle.Directions[4]));
		});
		test("actor art composes explicit COF components for each mapped direction", () =>
		{
			var request = Request(); request = request with { Motions = request.Motions.Select(m => m with { Path = "actor.cof", Layers = new() { [0] = "a.dcc", [1] = "b.dcc" } }).ToArray() };
			var art = LegacyActorArt.Load(request, Read); Check(art.Motions[ActorMotion.Walk].Directions[0].Frames.Count == 2);
			request.Motions[0].Layers!.Remove(1); Bad(() => LegacyActorArt.Load(request, Read));
		});
		test("actor art rejects missing, duplicate or unknown motions and direction indices", () =>
		{
			var good = Request();
			foreach (var bad in new[] { good with { Motions = good.Motions[..4] }, good with { Motions = [good.Motions[0], good.Motions[0], .. good.Motions[2..]] },
				good with { Motions = [good.Motions[0] with { Motion = "0" }, .. good.Motions[1..]] },
				good with { Motions = [good.Motions[0] with { Directions = [9, 9, 9, 9, 9, 9, 9, 9] }, .. good.Motions[1..]] },
				good with { Motions = [good.Motions[0] with { Fps = double.NaN }, .. good.Motions[1..]] },
				good with { Motions = [good.Motions[0] with { Layers = new() }, .. good.Motions[1..]] } }) Bad(() => LegacyActorArt.Load(bad, Read));
		});
		test("actor animation follows motion priority, direction, pause and death hold", () =>
		{
			var art = LegacyActorArt.Load(Request(), Read); var clock = new ActorAnimationClock(); var state = new EntityState(new(1), new(1), new(384, 384));
			Check(clock.Frame(state, 0, art) == 0 && clock.Frame(state, 3, art) == 1 && clock.Frame(state, 3, art) == 1);
			state = state with { MoveX = 1 }; Check(clock.Frame(state, 4, art) == 0 && clock.Motion == ActorMotion.Walk && clock.Facing == 3);
			clock.Face(-1, 0);
			state = state with { AttackCooldown = 12 }; clock.Frame(state, 5, art); Check(clock.Motion == ActorMotion.Attack && clock.Facing == 7 && clock.Frame(state, 50, art) == 1);
			state = state with { HitStun = 2 }; clock.Frame(state, 51, art); Check(clock.Motion == ActorMotion.Hit);
			state = state with { Health = 0 }; Check(clock.Frame(state, 52, art) == 0 && clock.Motion == ActorMotion.Death && clock.Frame(state, 1000, art) == 1);
			Check(clock.Frame(state, 0, art) == 0); Bad(() => clock.Frame(state, -1, art));
		});
		test("actor art validates path and input bounds before decode", () =>
		{
			Bad(() => LegacyActorArt.Load(Request() with { PalettePath = "../pal.dat" }, _ => throw new Exception("Unexpected read.")));
			Bad(() => LegacyActorArt.Load(Request(), _ => new byte[AssetDecoders.MaxInputBytes + 1]));
		});
		test("scene binds artwork to known actors and includes source bytes in identity", () =>
		{
			var request = PlaySceneContracts.Request() with { Artwork = [Request()] };
			byte[] Load(string path) => path.EndsWith(".dcc") ? Read(path) : PlayAssetContracts.Read(path);
			var scene = LegacyPlayScene.Load(request, Load); Check(scene.Artwork.Count == 1 && scene.ArtworkSources.Count == 6);
			var other = LegacyPlayScene.Load(request with { Artwork = null }, Load); Check(other.ContentId != scene.ContentId);
			Bad(() => LegacyPlayScene.Load(request with { Artwork = [Request() with { Entity = 999 }] }, Load));
			Bad(() => LegacyPlayScene.Load(request with { Artwork = [Request(), Request()] }, Load));
		});
	}
}
