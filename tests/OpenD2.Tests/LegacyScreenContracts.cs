using System.Security.Cryptography;
using OpenD2.Assets;

// Synthetic only; the genuine-data checks live in GenuineDataContracts and need D2_DATA_PATH.
internal static class LegacyScreenContracts
{
	static void Check(bool value, string what) { if (!value) throw new Exception("Assertion failed " + what); }

	public static void Run(Action<string, Action> test)
	{
		test("legacy screen: whole-number letterbox layout, never below 1x", () =>
		{
			Check(LetterboxLayout.Fit(1920, 1080, 800, 600) == new LetterboxLayout(1, 560, 240, 800, 600, 1920, 1080), "1080p 800x600 is 1x (2x needs 1600x1200)");
			Check(LetterboxLayout.Fit(1920, 1080, 640, 480) == new LetterboxLayout(2, 320, 60, 1280, 960, 1920, 1080), "1080p 640x480 is 2x");
			Check(LetterboxLayout.Fit(1600, 1200, 800, 600) is { Scale: 2, X: 0, Y: 0 }, "exact 2x");
			Check(LetterboxLayout.Fit(2560, 1440, 800, 600) is { Scale: 2, X: 480, Y: 120 }, "1440p");
			Check(LetterboxLayout.Fit(700, 500, 800, 600) is { Scale: 1, X: -50, Y: -50 }, "small window crops at 1x");
			Check(LegacyResolution.R640x480.Size() == (640, 480) && LegacyResolutions.Parse("800X600") == LegacyResolution.R800x600, "resolutions");
			try { LegacyResolutions.Parse("1024x768"); throw new Exception("odd resolution accepted"); } catch (ArgumentException) { }
		});
		test("legacy screen: nearest-neighbour present repeats source pixels and fills bars with pure black", () =>
		{
			byte[] rgb = [10, 20, 30, 40, 50, 60]; // 2x1
			var layout = LetterboxLayout.Fit(6, 4, 2, 1); // scale 3 (6/2=3, 4/1=4), y = (4-3)/2 = 0
			var window = LegacyFrameBuffer.Present(rgb, 2, 1, layout);
			Check(layout is { Scale: 3, X: 0, Y: 0, Height: 3 }, "layout");
			for (int y = 0; y < 3; y++) for (int x = 0; x < 6; x++) Check(window[(y * 6 + x) * 3] == (x < 3 ? 10 : 40), $"pixel {x},{y}");
			Check(window.AsSpan(3 * 6 * 3).ToArray().All(b => b == 0), "bottom bar black");
		});
		test("legacy screen: frame buffer clips, skips transparent pixels and leaves uncovered pixels black", () =>
		{
			var fb = new LegacyFrameBuffer(LegacyResolution.R640x480);
			var frame = new Dc6Frame(2, 2, 0, 0, [5, 6, 7, 8], [255, 0, 255, 255]);
			fb.Draw(frame, 639, 479); fb.Draw(frame, 0, 0);
			Check(fb.Coverage.Count(c => c != 0) == 4, "1 clipped + 3 opaque");
			var palette = Palette.Parse(MpqPipelineContracts.PaletteBytes());
			var rgb = fb.ToRgb(palette);
			Check(rgb[0] == 7 && rgb[1] == 250 && rgb[2] == 5 && rgb[3 * 1] == 0 && rgb[3 * 1 + 1] == 0 && rgb[3 * 1 + 2] == 0, "drawn index vs transparent black");
			Check(rgb[(479 * 640 + 639) * 3] == 7, "clipped corner");
		});
		test("legacy screen: capture is byte-identical across runs and sized to the selected resolution", () =>
		{
			var files = new Dictionary<string, byte[]>
			{
				["data\\global\\palette\\units\\pal.dat"] = MpqPipelineContracts.PaletteBytes(),
				[LegacyScreenRenderer.Scenes["gameselect"]] = LegacyFormatContracts.Dc6(),
			};
			byte[] Read(string p) => files.TryGetValue(p, out var b) ? b : throw new FileNotFoundException(p);
			var request = new LegacyCaptureRequest("gameselect", LegacyResolution.R800x600, "units", Seed: 7, Tick: 3);
			var a = LegacyScreenRenderer.EncodePng(LegacyScreenRenderer.Render(request, Read));
			var b = LegacyScreenRenderer.EncodePng(LegacyScreenRenderer.Render(request, Read));
			Check(a.AsSpan().SequenceEqual(b), "identical bytes");
			var decoded = PngReader.DecodeRgb(a); Check(decoded.Width == 800 && decoded.Height == 600, "800x600");
			Check(PngReader.DecodeRgb(LegacyScreenRenderer.EncodePng(LegacyScreenRenderer.Render(request with { Resolution = LegacyResolution.R640x480 }, Read))).Width == 640, "640x480");
			var windowed = LegacyScreenRenderer.Render(request with { WindowWidth = 1920, WindowHeight = 1080 }, Read);
			Check(windowed is { Width: 1920, Height: 1080, Layout.Scale: 1, Layout.X: 560 } && windowed.Rgb.AsSpan(0, 560 * 3).ToArray().All(v => v == 0), "windowed letterbox");
			foreach (var bad in new[] { request with { Scene = "nope" }, request with { Palette = "..\\x" } })
				try { LegacyScreenRenderer.Render(bad, Read); throw new Exception("bad request accepted"); } catch (ArgumentException) { }
		});
		test("screen diff: identical images score 100% / 0 / 1; changes lower match, raise error and lower SSIM", () =>
		{
			int w = 16, h = 16; var a = new byte[w * h * 3]; for (int i = 0; i < a.Length; i++) a[i] = (byte)(i * 7);
			var same = ScreenDiff.Compare(w, h, a, a);
			Check(same.MatchPercent == 100 && same.MeanAbsoluteError == 0 && same.Ssim == 1 && same.DifferentPixels == 0, "identical");
			var b = (byte[])a.Clone(); b[0] ^= 0xff; b[1] = (byte)(b[1] + 30);
			var one = ScreenDiff.Compare(w, h, a, b);
			Check(one.DifferentPixels == 1 && Math.Abs(one.MatchPercent - 100.0 * 255 / 256) < 1e-9 && one.Ssim < 1 && one.MeanAbsoluteError > 0, "one pixel");
			var black = new byte[a.Length]; Check(ScreenDiff.Compare(w, h, a, black).Ssim < 0.5, "very different");
			try { ScreenDiff.Compare(w, h, a, new byte[3]); throw new Exception("size mismatch accepted"); } catch (ArgumentException) { }
		});
		test("png reader round-trips the PNG writer and rejects malformed files", () =>
		{
			var rgba = new byte[5 * 3 * 4]; for (int i = 0; i < rgba.Length; i++) rgba[i] = (byte)(i * 13);
			var (w, h, rgb) = PngReader.DecodeRgb(PngWriter.EncodeRgba(5, 3, rgba));
			Check(w == 5 && h == 3 && Enumerable.Range(0, 15).All(i => rgb[i * 3] == rgba[i * 4] && rgb[i * 3 + 2] == rgba[i * 4 + 2]), "round trip");
			try { PngReader.DecodeRgb(new byte[20]); throw new Exception("garbage accepted"); } catch (InvalidDataException) { }
			var png = PngWriter.EncodeRgba(5, 3, rgba);
			try { PngReader.DecodeRgb(png.AsSpan(0, png.Length - 20)); throw new Exception("truncated accepted"); } catch (Exception e) when (e is InvalidDataException or EndOfStreamException) { }
		});
	}

	// Genuine checks, called from GenuineDataContracts when D2_DATA_PATH is set. Only hashes are stored here.
	// The expected hash is the RGB of an independent Python DC6 renderer (row-major 4-column stitch, units/act1 pal.dat).
	public const string GameSelectUnitsRgbSha256 = "9ec1529c911158ec76fc96854bb774f8edeb660b2884d014d554ff6ffd4fd02f";
	public const string GameSelectAct1RgbSha256 = "4bc657d0cb93ad1c1ef91b521e3ac93706ae78b6c0919aef1924fcd06ee32106";
	public static void Genuine(string data, Action<string, Action> test)
	{
		test("genuine: gameselect capture with the units palette matches the Python reference 100% and is byte-identical across runs", () =>
		{
			using var mpq = new MpqAssetSource(data);
			byte[] Read(string p) => mpq.TryRead(p, AssetDecoders.MaxInputBytes, out var b) ? b : throw new FileNotFoundException(p);
			var request = new LegacyCaptureRequest("gameselect", LegacyResolution.R800x600, "units");
			var units = LegacyScreenRenderer.Render(request, Read);
			Check(units.Width == 800 && units.Height == 600, "size");
			Check(Convert.ToHexString(SHA256.HashData(units.Rgb)).ToLowerInvariant() == GameSelectUnitsRgbSha256, "units RGB equals the Python reference");
			Check(LegacyScreenRenderer.EncodePng(units).AsSpan().SequenceEqual(LegacyScreenRenderer.EncodePng(LegacyScreenRenderer.Render(request, Read))), "byte-identical PNG");
			var act1 = LegacyScreenRenderer.Render(request with { Palette = "act1" }, Read);
			Check(Convert.ToHexString(SHA256.HashData(act1.Rgb)).ToLowerInvariant() == GameSelectAct1RgbSha256, "act1 RGB equals the Python reference");
			var diff = ScreenDiff.Compare(800, 600, units.Rgb, act1.Rgb);
			Check(diff.DifferentPixels == 13769, $"act1 vs units differ in {diff.DifferentPixels} pixels");
			var charSelect = LegacyScreenRenderer.Render(request with { Scene = "charselect" }, Read);
			Check(charSelect.Width == 800 && charSelect.Rgb.Any(v => v != 0), "character creation background renders");
		});
	}
}
