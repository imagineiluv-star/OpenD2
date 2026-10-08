using System.Buffers.Binary;
using OpenD2.Assets;

internal static class NpcArtContracts
{
	internal static LegacyNpcRequest Request(bool cof = false) => new(10, "data/global/palette/units/pal.dat",
		new("Idle", cof ? "npc.cof" : "npc.dcc", cof ? new() { [0] = "a.dcc", [1] = "b.dcc" } : null, [0, 0, 0, 0, 1, 1, 1, 1], 10), 4);
	private static byte[] Read(string path) => path.EndsWith(".dcc") || path.EndsWith(".cof") ? ActorArtContracts.Read(path) : PlayAssetContracts.Read(path);
	private static void Check(bool value) { if (!value) throw new Exception("NPC artwork assertion failed."); }
	private static void Bad(Action action)
	{ try { action(); } catch (Exception e) when (e is ArgumentException or InvalidDataException) { return; } throw new Exception("Expected invalid NPC artwork."); }
	private static byte[] LargeDcc(int size)
	{
		// A legal unused gap before direction data increases input without adding decoded pixels.
		// Appending bytes would instead make the final direction's bitstream invalid.
		var source = AnimationContracts.Dcc(); int header = 15 + source[2] * 4, gap = size - source.Length;
		var result = new byte[size]; source.AsSpan(0, header).CopyTo(result); source.AsSpan(header).CopyTo(result.AsSpan(header + gap));
		for (int i = 0; i < source[2]; i++) BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(15 + i * 4), BinaryPrimitives.ReadInt32LittleEndian(source.AsSpan(15 + i * 4)) + gap);
		return result;
	}
	public static void Run(string root, Action<string, Action> test)
	{
		test("NPC DCC and COF use one idle motion and tick-based looping with pause/reset", () =>
		{
			foreach (bool cof in new[] { false, true })
			{
				var art = LegacyActorArt.LoadNpc(Request(cof), Read);
				Check(art.Motions.Count == 1 && art.IdleFrame(0, 4) == 0 && art.IdleFrame(3, 4) == 1 && art.IdleFrame(3, 4) == 1 && art.IdleFrame(5, 4) == 0 && art.IdleFrame(0, 4) == 0);
				Bad(() => art.IdleFrame(-1, 0)); Bad(() => art.IdleFrame(0, 8));
			}
		});
		test("NPC artwork rejects invalid facing, non-idle motion, missing motion and unsafe paths", () =>
		{
			var good = Request();
			foreach (var bad in new[] { good with { Facing = -1 }, good with { Facing = 8 }, good with { Entity = 0 }, good with { Idle = null! },
				good with { Idle = good.Idle with { Motion = "Attack" } }, good with { Idle = good.Idle with { Path = "../npc.dcc" } } })
				Bad(() => LegacyActorArt.LoadNpc(bad, _ => throw new Exception("Invalid NPC definition reached I/O.")));
		});
		test("NPC artwork must reference the quest giver without accepting combat actor IDs", () =>
		{
			foreach (uint id in new uint[] { 0, 1, 2, 999 })
				Bad(() => LegacyPlayScene.Load(PlaySceneContracts.Request() with { NpcArtwork = Request() with { Entity = id } }, _ => throw new Exception("Invalid NPC ownership reached I/O.")));
		});
		test("optional NPC artwork changes presentation identity without adding combat actors or changing simulation", () =>
		{
			var request = PlaySceneContracts.Request(); var before = LegacyPlayScene.Load(request, Read);
			var after = LegacyPlayScene.Load(request with { NpcArtwork = Request() }, Read);
			Check(before.NpcArtwork is null && before.NpcArtworkSources.Count == 0 && after.NpcArtwork is not null && after.NpcFacing == 4 && after.NpcArtworkSources.Count == 2);
			Check(before.ContentId != after.ContentId && before.World.ContentHash == after.World.ContentHash && before.Actors.Count == after.Actors.Count);
			var a = before.Create(1); var b = after.Create(1);
			for (int i = 0; i < 60; i++) { a.Step(); b.Step(); Check(a.ComputeStateHash() == b.ComputeStateHash()); }
			Check(!PlaySceneReadiness.Check(before).ReadyForAllSpritesGuiCheck);
		});
		test("NPC facing and source bytes affect identity while caller mutation cannot change the loaded snapshot", () =>
		{
			var request = PlaySceneContracts.Request() with { NpcArtwork = Request(true) };
			var original = LegacyPlayScene.Load(request, Read);
			Check(original.ContentId != LegacyPlayScene.Load(request with { NpcArtwork = request.NpcArtwork with { Facing = 0 } }, Read).ContentId);
			Check(original.ContentId != LegacyPlayScene.Load(request, p => { var bytes = Read(p); if (p.Contains("units")) bytes[0] = 12; return bytes; }).ContentId);
			var snapshot = LegacyPlayScene.Load(request, p =>
			{
				request.NpcArtwork.Idle.Directions[0] = 31; request.NpcArtwork.Idle.Layers![0] = "missing.dcc";
				return Read(p);
			});
			Check(snapshot.ContentId == original.ContentId);
		});
		test("NPC source bytes share the aggregate scene input budget with combat artwork", () =>
		{
			var request = PlaySceneContracts.Request() with { Artwork = [ActorArtContracts.Request(), ActorArtContracts.Request() with { Entity = 2 }], NpcArtwork = Request() };
			var actor = LargeDcc(10 * 1024 * 1024); var npc = LargeDcc(32 * 1024 * 1024);
			byte[] Load(string p) => p == "npc.dcc" ? npc : p.EndsWith(".dcc") ? actor : Read(p);
			Check(LegacyPlayScene.Load(request with { NpcArtwork = null }, Load).Artwork.Count == 2);
			try { LegacyPlayScene.Load(request, Load); throw new Exception("Expected shared input budget failure."); }
			catch (InvalidDataException error) when (error.Message.Contains("Scene input exceeds")) { }
		});
		test("NPC scene copy round trips and reports all-sprite readiness separately from combat readiness", () =>
		{
			var request = PlaySceneContracts.Request() with { Artwork = [ActorArtContracts.Request(), ActorArtContracts.Request() with { Entity = 2 }] };
			var combatOnly = PlaySceneReadiness.Check(LegacyPlayScene.Load(request, Read));
			Check(combatOnly.ReadyForSceneGuiCheck && !combatOnly.NpcArtworkConfigured && !combatOnly.ReadyForAllSpritesGuiCheck);
			string file = Path.Combine(root, "npc-art-scene.json"); var complete = LegacySceneSetup.SaveNew(file, request with { NpcArtwork = Request() }, Read);
			var restored = LegacySceneRequest.Read(file); Check(restored.NpcArtwork!.Facing == 4 && restored.NpcArtwork.Idle.Motion == "Idle");
			Check(complete.NpcArtworkConfigured && complete.ReadyForAllSpritesGuiCheck && !complete.OriginalRulesValidated && complete.GuiQa == "NOT_RUN");
			Check(LegacyPlayScene.Load(restored, Read).ContentId == complete.ContentId);
		});
	}
}
