using System.Buffers.Binary;
using OpenD2.Assets;

internal static class HudArtContracts
{
	internal static LegacyHudRequest Request() => new("data/global/palette/units/pal.dat", 12, 2,
		[new("Health", "hud.dc6", 0, 0, 0), new("Menu", "hud.dc6", 0, 3, 0), new("Inventory", "hud.dc6", 0, 6, 0), new("Decoration", "hud.dc6", 0, 9, 0)]);
	private static byte[] Read(string path) => path.EndsWith(".dc6") ? LegacyFormatContracts.Dc6() : path.EndsWith(".dcc") ? ActorArtContracts.Read(path) : PlayAssetContracts.Read(path);
	private static void Check(bool value) { if (!value) throw new Exception("HUD assertion failed."); }
	private static void Bad(Action action)
	{ try { action(); } catch (Exception e) when (e is ArgumentException or InvalidDataException) { return; } throw new Exception("Expected invalid HUD."); }
	private static byte[] LargeFrame()
	{
		// Transparent 4096x1024 frame, with exact row terminators. Only decoded pixel storage is large.
		const int row = 34; var bytes = new byte[60 + row * 1024];
		void U32(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
		U32(0, 6); U32(16, 1); U32(20, 1); U32(24, 28); U32(32, 4096); U32(36, 1024); U32(56, row * 1024);
		for (int y = 0; y < 1024; y++) { bytes.AsSpan(60 + y * row, 32).Fill(255); bytes[60 + y * row + 32] = 160; bytes[60 + y * row + 33] = 128; }
		return bytes;
	}
	public static void Run(string root, Action<string, Action> test)
	{
		test("HUD loads explicit DC6 frames and palette, preserves alpha, shares repeated source frames", () =>
		{
			int reads = 0; var art = LegacyHudArt.Load(Request(), p => { reads++; return Read(p); });
			Check(reads == 2 && art.Sprites.Count == 4 && art.PixelCount == 6 && art.HasHealth);
			Check(ReferenceEquals(art.Sprites[0].Frame, art.Sprites[1].Frame));
			Check(art.Sprites[0].X == 0 && art.Sprites[0].Frame.OffsetX == -5 && art.Sprites[1].X == 3);
			Check(art.Sprites[0].Frame.Opacity.SequenceEqual(new byte[] { 0, 255, 0, 255, 255, 0 }));
		});
		test("HUD rejects unsafe or ambiguous definitions before resource I/O", () =>
		{
			var good = Request(); var element = good.Elements[0];
			foreach (var bad in new[] { good with { Width = 0 }, good with { Height = 1025 }, good with { Elements = [] }, good with { Elements = null! },
				good with { Elements = [null!] }, good with { PalettePath = "../pal.dat" }, good with { Elements = Enumerable.Repeat(element, 33).ToArray() },
				good with { Elements = [element, element] }, good with { Elements = [element with { Role = "0" }] }, good with { Elements = [element with { Role = "Mana" }] },
				good with { Elements = [element with { Path = "../ui.dc6" }] }, good with { Elements = [element with { X = -1 }] }, good with { Elements = [element with { Frame = -1 }] } })
				Bad(() => LegacyHudArt.Load(bad, _ => throw new Exception("Invalid HUD reached I/O.")));
		});
		test("HUD rejects absent frames, frames beyond canvas and oversized source input", () =>
		{
			var good = Request(); var first = good.Elements[0];
			Bad(() => LegacyHudArt.Load(good with { Elements = [first with { Frame = 1 }] }, Read));
			Bad(() => LegacyHudArt.Load(good with { Elements = [first with { X = 11 }] }, Read));
			Bad(() => LegacyHudArt.Load(good, p => p.EndsWith(".dc6") ? new byte[AssetDecoders.MaxInputBytes + 1] : Read(p)));
		});
		test("HUD health clips bottom rows safely and only mapped buttons dispatch actions", () =>
		{
			Check(LegacyHudArt.FilledRows(0, 100, 32) == 0 && LegacyHudArt.FilledRows(50, 100, 32) == 16 && LegacyHudArt.FilledRows(100, 100, 32) == 32);
			Check(LegacyHudArt.FilledRows(-1, 100, 32) == 0 && LegacyHudArt.FilledRows(int.MaxValue, 100, 32) == 32 && LegacyHudArt.FilledRows(int.MaxValue, int.MaxValue, 4096) == 4096);
			Bad(() => LegacyHudArt.FilledRows(1, 0, 32));
			var art = LegacyHudArt.Load(Request(), Read);
			Check(art.ActionAt(0, 0) is null && art.ActionAt(3, 0) == HudRole.Menu && art.ActionAt(6, 0) == HudRole.Inventory && art.ActionAt(9, 0) is null);
			Check(art.ActionAt(3, 2) is null && art.ActionAt(-1, 0) is null && art.ActionAt(double.NaN, 0) is null);
		});
		test("HUD source and layout affect scene identity while input mutation cannot alter snapshot", () =>
		{
			var request = PlaySceneContracts.Request() with { HudArtwork = Request() }; var before = LegacyPlayScene.Load(request, Read);
			var changed = request.HudArtwork with { Width = 13 };
			Check(before.ContentId != LegacyPlayScene.Load(request with { HudArtwork = changed }, Read).ContentId);
			Check(before.ContentId != LegacyPlayScene.Load(request, p => { var bytes = Read(p); if (p.Contains("units")) bytes[0] = 12; return bytes; }).ContentId);
			var snapshot = LegacyPlayScene.Load(request, p => { request.HudArtwork.Elements[0] = new("Health", "missing.dc6", 9, 10, 0); return Read(p); });
			Check(snapshot.ContentId == before.ContentId && before.HudArtworkSources.Count == 2);
		});
		test("optional HUD keeps authoritative world and simulation unchanged and does not claim GUI readiness", () =>
		{
			var request = PlaySceneContracts.Request(); var before = LegacyPlayScene.Load(request, Read); var after = LegacyPlayScene.Load(request with { HudArtwork = Request() }, Read);
			Check(before.HudArtwork is null && before.HudArtworkSources.Count == 0 && before.World.ContentHash == after.World.ContentHash && before.ContentId != after.ContentId);
			var a = before.Create(1); var b = after.Create(1); for (int i = 0; i < 60; i++) { a.Step(); b.Step(); Check(a.ComputeStateHash() == b.ComputeStateHash()); }
			var ready = PlaySceneReadiness.Check(after); Check(ready.HudArtworkConfigured && !ready.ReadyForAllSpritesGuiCheck && !ready.OriginalRulesValidated && ready.GuiQa == "NOT_RUN");
		});
		test("HUD scene copy round trips, preserves source and refuses invalid replacement", () =>
		{
			var request = PlaySceneContracts.Request() with { HudArtwork = Request() }; string file = Path.Combine(root, "hud-scene.json");
			var ready = LegacySceneSetup.SaveNew(file, request, Read); byte[] before = File.ReadAllBytes(file);
			Check(LegacyPlayScene.Load(LegacySceneRequest.Read(file), Read).ContentId == ready.ContentId);
			Bad(() => LegacySceneSetup.SaveNew(file, request with { HudArtwork = request.HudArtwork with { Width = 1 } }, Read));
			Check(File.ReadAllBytes(file).SequenceEqual(before));
		});
		test("HUD source bytes share the 128 MiB scene budget with combat artwork", () =>
		{
			var request = PlaySceneContracts.Request() with { Artwork = [ActorArtContracts.Request(), ActorArtContracts.Request() with { Entity = 2 }], HudArtwork = Request() };
			var actor = NpcArtContracts.LargeDcc(10 * 1024 * 1024); var hud = LegacyFormatContracts.Dc6(); Array.Resize(ref hud, AssetDecoders.MaxInputBytes);
			byte[] Load(string p) => p.EndsWith(".dc6") ? hud : p.EndsWith(".dcc") ? actor : Read(p);
			Check(LegacyPlayScene.Load(request with { HudArtwork = null }, Load).Artwork.Count == 2);
			try { LegacyPlayScene.Load(request, Load); throw new Exception("Expected scene input rejection."); }
			catch (InvalidDataException error) when (error.Message.Contains("Scene input exceeds")) { }
		});
		test("HUD unique decoded frames respect the pixel budget without charging repeated placements", () =>
		{
			var request = new LegacyHudRequest(Request().PalettePath, 4096, 1024, [new("Decoration", "a.dc6", 0, 0, 0), new("Decoration", "a.dc6", 0, 0, 0)]);
			var bytes = LargeFrame(); byte[] Load(string p) => p.EndsWith(".dc6") ? bytes : Read(p);
			Check(LegacyHudArt.Load(request, Load).PixelCount == LegacyHudArt.MaxPixels);
			request.Elements[1] = request.Elements[1] with { Path = "b.dc6" };
			try { LegacyHudArt.Load(request, Load); throw new Exception("Expected HUD pixel rejection."); }
			catch (InvalidDataException error) when (error.Message.Contains("HUD decoded pixel budget")) { }
		});
	}
}
