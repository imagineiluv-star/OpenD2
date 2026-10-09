using System.Buffers.Binary;
using OpenD2.Assets;
using OpenD2.Core;

internal static class ItemArtContracts
{
	private static LegacyItemRequest Request() => new("data/global/palette/units/pal.dat",
		[new("TrainingSword", "items.dc6", 0), new("TrainingVest", "ITEMS.DC6", 0)]);
	private static byte[] Read(string path) => path.EndsWith(".dc6") ? LegacyFormatContracts.Dc6() : path.EndsWith(".dcc") ? ActorArtContracts.Read(path) : PlayAssetContracts.Read(path);
	private static void Check(bool value) { if (!value) throw new Exception("Item artwork assertion failed."); }
	private static void Bad(Action action)
	{ try { action(); } catch (Exception e) when (e is ArgumentException or InvalidDataException) { return; } throw new Exception("Expected invalid item artwork."); }
	public static void Run(string root, Action<string, Action> test)
	{
		test("item icons share normalized source/frame storage and preserve opacity including opaque zero", () =>
		{
			var paths = new List<string>(); var art = LegacyItemArt.Load(Request(), p => { paths.Add(p); return Read(p); });
			var sword = art.Icons[ItemDefinition.TrainingSword]; var vest = art.Icons[ItemDefinition.TrainingVest];
			Check(paths.Count == 2 && paths[1] == "items.dc6" && ReferenceEquals(sword, vest) && art.PixelCount == 6);
			Check(sword.OffsetX == -5 && sword.OffsetY == 7 && sword.Opacity.SequenceEqual(new byte[] { 0, 255, 0, 255, 255, 0 }));
			var rgba = art.Palette.ToRgba(sword.Indices, sword.Opacity); Check(rgba[3] == 0 && rgba[15] == 255 && sword.Indices[3] == 0);
		});
		test("item icon requests reject unknown/duplicate catalog IDs and unsafe paths before I/O", () =>
		{
			var good = Request(); var first = good.Icons[0];
			foreach (var bad in new[] { good with { Icons = null! }, good with { Icons = [] }, good with { Icons = [null!] },
				good with { Icons = [first, first] }, good with { Icons = [first, first, first] }, good with { PalettePath = "../pal.dat" },
				good with { Icons = [first with { Definition = "0" }] }, good with { Icons = [first with { Definition = "trainingsword" }] },
				good with { Icons = [first with { Definition = "Unknown" }] }, good with { Icons = [first with { Path = "../item.dc6" }] },
				good with { Icons = [first with { Path = "item.dcc" }] }, good with { Icons = [first with { Frame = -1 }] }, good with { Icons = [first with { Frame = 4096 }] } })
				Bad(() => LegacyItemArt.Load(bad, _ => throw new Exception("Invalid item request reached I/O.")));
		});
		test("item DC6 frame selection is direction-major and bounded before allocating pixel planes", () =>
		{
			var one = LegacyFormatContracts.Dc6(); var bytes = new byte[40 + 4 * 44]; one.AsSpan(0, 24).CopyTo(bytes);
			void U32(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
			U32(16, 2); U32(20, 2);
			for (int i = 0; i < 4; i++) { int at = 40 + i * 44; U32(24 + i * 4, (uint)at); one.AsSpan(28).CopyTo(bytes.AsSpan(at)); bytes[at + 34] = (byte)(10 + i); }
			var good = Request() with { Icons = [new("TrainingSword", "items.dc6", 3)] };
			var art = LegacyItemArt.Load(good, p => p.EndsWith(".dc6") ? bytes : Read(p));
			Check(art.Icons[ItemDefinition.TrainingSword].Indices[4] == 13);
			Bad(() => LegacyItemArt.Load(good with { Icons = [good.Icons[0] with { Frame = 4 }] }, p => p.EndsWith(".dc6") ? bytes : Read(p)));
			var wide = LegacyFormatContracts.Dc6(); BinaryPrimitives.WriteUInt32LittleEndian(wide.AsSpan(32), 256);
			var single = good with { Icons = [good.Icons[0] with { Frame = 0 }] };
			Check(LegacyItemArt.Load(single, p => p.EndsWith(".dc6") ? wide : Read(p)).PixelCount == 512);
			BinaryPrimitives.WriteUInt32LittleEndian(wide.AsSpan(32), 257);
			Bad(() => LegacyItemArt.Load(single, p => p.EndsWith(".dc6") ? wide : Read(p)));
			Bad(() => Dc6Image.Parse(one).DecodeFrame(0, 0, 0));
		});
		test("item artwork enforces per-file and total input budgets", () =>
		{
			Bad(() => LegacyItemArt.Load(Request(), p => p.EndsWith(".dc6") ? new byte[AssetDecoders.MaxInputBytes + 1] : Read(p)));
			var bytes = LegacyFormatContracts.Dc6(); Array.Resize(ref bytes, AssetDecoders.MaxInputBytes);
			var request = Request(); request.Icons[1] = request.Icons[1] with { Path = "other.dc6" };
			try { LegacyItemArt.Load(request, p => p.EndsWith(".dc6") ? bytes : Read(p)); throw new Exception("Expected item input rejection."); }
			catch (InvalidDataException e) when (e.Message.Contains("Item artwork input budget")) { }
		});
		test("item source bytes and mappings affect identity and caller mutation cannot alter a loading snapshot", () =>
		{
			var request = PlaySceneContracts.Request() with { ItemArtwork = Request() }; var before = LegacyPlayScene.Load(request, Read);
			Check(before.ItemArtworkSources.Count == 2);
			Check(before.ContentId != LegacyPlayScene.Load(request with { ItemArtwork = request.ItemArtwork with { Icons = [request.ItemArtwork.Icons[0]] } }, Read).ContentId);
			Check(before.ContentId != LegacyPlayScene.Load(request, p => { var bytes = Read(p); if (p.Contains("units")) bytes[0] = 12; return bytes; }).ContentId);
			Check(before.ContentId != LegacyPlayScene.Load(request, p => { var bytes = Read(p); if (p.EndsWith(".dc6")) bytes[62] = 8; return bytes; }).ContentId);
			var snapshot = LegacyPlayScene.Load(request, p => { request.ItemArtwork.Icons[0] = new("TrainingSword", "missing.dc6", 99); return Read(p); });
			Check(before.ContentId == snapshot.ContentId);
		});
		test("optional item artwork preserves simulation rules, old scene identity and readiness semantics", () =>
		{
			var request = PlaySceneContracts.Request(); var before = LegacyPlayScene.Load(request, Read); var after = LegacyPlayScene.Load(request with { ItemArtwork = Request() }, Read);
			Check(before.ItemArtwork is null && before.ItemArtworkSources.Count == 0 && before.ContentId == LegacyPlayScene.Load(request with { ItemArtwork = null }, Read).ContentId);
			Check(before.World.ContentHash == after.World.ContentHash && before.ContentId != after.ContentId);
			var a = before.Create(1); var b = after.Create(1); for (int i = 0; i < 60; i++) { a.Step(); b.Step(); Check(a.ComputeStateHash() == b.ComputeStateHash()); }
			var ready = PlaySceneReadiness.Check(after); Check(ready.ItemArtworkCount == 2 && !ready.ReadyForAllSpritesGuiCheck && !ready.OriginalRulesValidated && ready.GuiQa == "NOT_RUN");
		});
		test("item artwork scene copy round trips without replacing files or saving invalid frames", () =>
		{
			var request = PlaySceneContracts.Request() with { ItemArtwork = Request() }; string file = Path.Combine(root, "item-scene.json");
			var ready = LegacySceneSetup.SaveNew(file, request, Read); byte[] before = File.ReadAllBytes(file);
			Check(ready.ItemArtworkCount == 2 && LegacyPlayScene.Load(LegacySceneRequest.Read(file), Read).ContentId == ready.ContentId);
			try { LegacySceneSetup.SaveNew(file, request, Read); throw new Exception("Existing file was overwritten."); } catch (IOException) { }
			string invalid = Path.Combine(root, "invalid-item-scene.json");
			Bad(() => LegacySceneSetup.SaveNew(invalid, request with { ItemArtwork = request.ItemArtwork with { Icons = [request.ItemArtwork.Icons[0] with { Frame = 999 }] } }, Read));
			Check(!File.Exists(invalid) && File.ReadAllBytes(file).SequenceEqual(before));
		});
		test("item source bytes share the scene input budget with terrain and actors", () =>
		{
			var request = PlaySceneContracts.Request() with { Artwork = [ActorArtContracts.Request(), ActorArtContracts.Request() with { Entity = 2 }], ItemArtwork = Request() };
			var actor = NpcArtContracts.LargeDcc(10 * 1024 * 1024); var icon = LegacyFormatContracts.Dc6(); Array.Resize(ref icon, AssetDecoders.MaxInputBytes);
			byte[] Load(string p) => p.EndsWith(".dc6") ? icon : p.EndsWith(".dcc") ? actor : Read(p);
			Check(LegacyPlayScene.Load(request with { ItemArtwork = null }, Load).Artwork.Count == 2);
			try { LegacyPlayScene.Load(request, Load); throw new Exception("Expected scene input rejection."); }
			catch (InvalidDataException error) when (error.Message.Contains("Scene input exceeds")) { }
		});
	}
}
