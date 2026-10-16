using System.Buffers.Binary;
using System.IO.Compression;

namespace OpenD2.Assets;

/// <summary>The original game's fixed back-buffer sizes. 800x600 is the LoD default; 640x480 is the classic option.</summary>
public enum LegacyResolution { R800x600, R640x480 }

public static class LegacyResolutions
{
	public static (int Width, int Height) Size(this LegacyResolution r) => r == LegacyResolution.R640x480 ? (640, 480) : (800, 600);
	public static LegacyResolution Parse(string text) => text.Trim().ToLowerInvariant() switch
	{
		"800x600" => LegacyResolution.R800x600,
		"640x480" => LegacyResolution.R640x480,
		_ => throw new ArgumentException("Resolution must be 800x600 or 640x480."),
	};
}

/// <summary>Fit: largest aspect-preserving scale (may be fractional, e.g. 1.8x at 1920x1080). Integer: largest whole-number scale, pixel-perfect.</summary>
public enum LegacyScaleMode { Fit, Integer }
/// <summary>
/// Filter for fractional scales. Sharp (default): nearest upscale by the whole number at or above the target scale
/// (ceil, e.g. 2x for 1.8x), then a linear resample down to the fitted rect, so pixels stay crisp without uneven
/// nearest columns. Nearest and Linear sample the 800x600 buffer directly. At whole-number scales all three equal nearest.
/// </summary>
public enum LegacyScaleFilter { Sharp, Nearest, Linear }

public static class LegacyScaleOptions
{
	public static LegacyScaleMode ParseMode(string text) => text.Trim().ToLowerInvariant() switch
	{ "fit" => LegacyScaleMode.Fit, "integer" => LegacyScaleMode.Integer, _ => throw new ArgumentException("--scale must be fit or integer.") };
	public static LegacyScaleFilter ParseFilter(string text) => text.Trim().ToLowerInvariant() switch
	{ "sharp" => LegacyScaleFilter.Sharp, "nearest" => LegacyScaleFilter.Nearest, "linear" => LegacyScaleFilter.Linear, _ => throw new ArgumentException("--filter must be sharp, nearest or linear.") };
	/// <summary>The whole-number prescale the Sharp filter uses: ceil(scale), at least 1.</summary>
	public static int SharpPrescale(double scale) => Math.Max(1, (int)Math.Ceiling(scale - 1e-9));
}

/// <summary>Where the fixed screen lands in a window, centred; the uncovered pillarbox/letterbox area is pure black.</summary>
public readonly record struct LetterboxLayout(double Scale, int X, int Y, int Width, int Height, int WindowWidth, int WindowHeight)
{
	public bool IsInteger => Scale == Math.Floor(Scale);

	public static LetterboxLayout Fit(int windowWidth, int windowHeight, int screenWidth, int screenHeight, LegacyScaleMode mode = LegacyScaleMode.Fit)
	{
		if (windowWidth < 1 || windowHeight < 1 || screenWidth < 1 || screenHeight < 1) throw new ArgumentOutOfRangeException(nameof(windowWidth));
		double scale = mode == LegacyScaleMode.Integer
			// Never below 1: a window smaller than the screen shows the centred 1x image cropped.
			? Math.Max(1, Math.Min(windowWidth / screenWidth, windowHeight / screenHeight))
			: Math.Min((double)windowWidth / screenWidth, (double)windowHeight / screenHeight);
		int w = Math.Max(1, (int)Math.Round(screenWidth * scale)), h = Math.Max(1, (int)Math.Round(screenHeight * scale));
		return new(scale, (windowWidth - w) / 2, (windowHeight - h) / 2, w, h, windowWidth, windowHeight);
	}
}

/// <summary>
/// An 8-bit indexed surface with the original's fixed size. Pixels nothing was drawn on stay uncovered and
/// present as pure black. Drawing is CPU-only and deterministic, so captures do not depend on a GPU.
/// </summary>
public sealed class LegacyFrameBuffer
{
	public int Width { get; }
	public int Height { get; }
	public byte[] Indices { get; }
	public byte[] Coverage { get; }
	public LegacyFrameBuffer(LegacyResolution resolution)
	{
		(Width, Height) = resolution.Size();
		Indices = new byte[Width * Height]; Coverage = new byte[Width * Height];
	}

	public void Clear() { Array.Clear(Indices); Array.Clear(Coverage); }

	/// <summary>Draws a decoded frame with its top-left corner at (x, y), clipped to the surface. Transparent pixels are skipped.</summary>
	public void Draw(Dc6Frame frame, int x, int y)
	{
		for (int row = 0; row < frame.Height; row++)
		{
			int sy = y + row; if ((uint)sy >= (uint)Height) continue;
			for (int col = 0; col < frame.Width; col++)
			{
				int sx = x + col; if ((uint)sx >= (uint)Width) continue;
				int src = row * frame.Width + col; if (frame.Opacity[src] == 0) continue;
				int dst = sy * Width + sx; Indices[dst] = frame.Indices[src]; Coverage[dst] = 255;
			}
		}
	}

	/// <summary>
	/// Draws a full-screen background stored as tiles (e.g. 256x256 frames). Tiles go left to right; a row ends once
	/// its widths reach the 800-pixel design width, as the game's screen DC6s are laid out for 800x600. The 640x480
	/// option shows the top-left crop.
	/// </summary>
	public void DrawTiledBackground(Dc6Image image, int direction = 0, int designWidth = 800)
	{
		int x = 0, y = 0, rowHeight = 0;
		for (int f = 0; f < image.FramesPerDirection; f++)
		{
			var frame = image.DecodeFrame(direction, f);
			if (x >= designWidth) { x = 0; y += rowHeight; rowHeight = 0; }
			Draw(frame, x, y); x += frame.Width; rowHeight = Math.Max(rowHeight, frame.Height);
		}
	}

	/// <summary>RGB24 through a palette; uncovered pixels are (0,0,0).</summary>
	public byte[] ToRgb(Palette palette)
	{
		var rgba = palette.ToRgba(Indices, Coverage); var rgb = new byte[Width * Height * 3];
		for (int i = 0, n = Width * Height; i < n; i++)
			if (Coverage[i] != 0) { rgb[i * 3] = rgba[i * 4]; rgb[i * 3 + 1] = rgba[i * 4 + 1]; rgb[i * 3 + 2] = rgba[i * 4 + 2]; }
		return rgb;
	}

	/// <summary>
	/// Scales into a window-sized RGB24 image with black bars, sampling at window pixel centres like the GPU.
	/// Nearest: source = floor((offset + 0.5) * size / scaledSize). Linear: bilinear on the source. Sharp: bilinear on the
	/// source nearest-upscaled by <see cref="LegacyScaleOptions.SharpPrescale"/>. Whole-number scales give nearest for all.
	/// </summary>
	public static byte[] Present(ReadOnlySpan<byte> rgb, int width, int height, LetterboxLayout layout, LegacyScaleFilter filter = LegacyScaleFilter.Nearest)
	{
		if (rgb.Length != width * height * 3) throw new ArgumentException("RGB size does not match the screen size.");
		var output = new byte[layout.WindowWidth * layout.WindowHeight * 3];
		bool exact = layout.Width % width == 0 && layout.Height % height == 0 && layout.Width / width == layout.Height / height;
		if (exact) filter = LegacyScaleFilter.Nearest;
		int k = filter == LegacyScaleFilter.Sharp ? LegacyScaleOptions.SharpPrescale((double)layout.Width / width) : 1;
		int vw = width * k, vh = height * k; // virtual (prescaled) image for Sharp; the source itself for Linear
		for (int wy = Math.Max(0, layout.Y); wy < Math.Min(layout.WindowHeight, layout.Y + layout.Height); wy++)
		{
			int oy = wy - layout.Y;
			for (int wx = Math.Max(0, layout.X); wx < Math.Min(layout.WindowWidth, layout.X + layout.Width); wx++)
			{
				int ox = wx - layout.X; var dst = output.AsSpan((wy * layout.WindowWidth + wx) * 3, 3);
				if (filter == LegacyScaleFilter.Nearest)
				{
					int sx = (int)((2L * ox + 1) * width / (2L * layout.Width)), sy = (int)((2L * oy + 1) * height / (2L * layout.Height));
					rgb.Slice((sy * width + sx) * 3, 3).CopyTo(dst); continue;
				}
				double u = (ox + 0.5) * vw / layout.Width - 0.5, v = (oy + 0.5) * vh / layout.Height - 0.5;
				int x0 = (int)Math.Floor(u), y0 = (int)Math.Floor(v); double fx = u - x0, fy = v - y0;
				int X(int vx) => Math.Clamp(vx, 0, vw - 1) / k; int Y(int vy) => Math.Clamp(vy, 0, vh - 1) / k;
				for (int c = 0; c < 3; c++)
				{
					double p00 = rgb[(Y(y0) * width + X(x0)) * 3 + c], p10 = rgb[(Y(y0) * width + X(x0 + 1)) * 3 + c];
					double p01 = rgb[(Y(y0 + 1) * width + X(x0)) * 3 + c], p11 = rgb[(Y(y0 + 1) * width + X(x0 + 1)) * 3 + c];
					double top = p00 + (p10 - p00) * fx, bottom = p01 + (p11 - p01) * fx;
					dst[c] = (byte)Math.Clamp((int)Math.Round(top + (bottom - top) * fy), 0, 255);
				}
			}
		}
		return output;
	}
}

/// <summary>Without a window size the capture is the internal 800x600/640x480 buffer (scale options do not apply); with one it is the presented window.</summary>
public sealed record LegacyCaptureRequest(string Scene, LegacyResolution Resolution, string Palette, int Seed = 0, int Tick = 0, int? WindowWidth = null, int? WindowHeight = null, LegacyScaleMode ScaleMode = LegacyScaleMode.Fit, LegacyScaleFilter Filter = LegacyScaleFilter.Sharp);
public sealed record LegacyCapture(int Width, int Height, byte[] Rgb, LetterboxLayout? Layout, IReadOnlyList<string> Sources);

/// <summary>Renders the named original-mode screens for headless capture. Static screens ignore Seed and Tick.</summary>
public static class LegacyScreenRenderer
{
	public static readonly IReadOnlyDictionary<string, string> Scenes = new SortedDictionary<string, string>(StringComparer.Ordinal)
	{
		["gameselect"] = "data\\global\\ui\\frontend\\gameselectscreenexp.dc6",
		["charselect"] = "data\\global\\ui\\frontend\\charactercreationscreenexp.dc6",
	};

	public static LegacyCapture Render(LegacyCaptureRequest request, Func<string, byte[]> read)
	{
		if (!Scenes.TryGetValue(request.Scene, out var path)) throw new ArgumentException($"Unknown scene '{request.Scene}'. Known: {string.Join(", ", Scenes.Keys)}.");
		if (request.Palette.Length == 0 || !request.Palette.All(c => char.IsAsciiLetterOrDigit(c))) throw new ArgumentException("Palette must be a palette folder name such as units or act1.");
		string palettePath = $"data\\global\\palette\\{request.Palette}\\pal.dat";
		var palette = Palette.Parse(read(palettePath));
		var screen = new LegacyFrameBuffer(request.Resolution);
		screen.DrawTiledBackground(Dc6Image.Parse(read(path)));
		var rgb = screen.ToRgb(palette);
		if (request.WindowWidth is { } ww && request.WindowHeight is { } wh)
		{
			var layout = LetterboxLayout.Fit(ww, wh, screen.Width, screen.Height, request.ScaleMode);
			return new(ww, wh, LegacyFrameBuffer.Present(rgb, screen.Width, screen.Height, layout, request.Filter), layout, [path, palettePath]);
		}
		return new(screen.Width, screen.Height, rgb, null, [path, palettePath]);
	}

	public static byte[] EncodePng(LegacyCapture capture)
	{
		var rgba = new byte[capture.Width * capture.Height * 4];
		for (int i = 0, n = capture.Width * capture.Height; i < n; i++)
		{ rgba[i * 4] = capture.Rgb[i * 3]; rgba[i * 4 + 1] = capture.Rgb[i * 3 + 1]; rgba[i * 4 + 2] = capture.Rgb[i * 3 + 2]; rgba[i * 4 + 3] = 255; }
		return PngWriter.EncodeRgba(capture.Width, capture.Height, rgba);
	}
}

public sealed record ScreenDiffResult(int Width, int Height, long Pixels, long MatchingPixels, double MatchPercent, double MeanAbsoluteError, double Ssim, long DifferentPixels);

/// <summary>Compares two equally sized RGB images: exact pixel match %, mean absolute error per channel (0-255), and SSIM.</summary>
public static class ScreenDiff
{
	public static ScreenDiffResult Compare(int width, int height, ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
	{
		long n = (long)width * height;
		if (a.Length != n * 3 || b.Length != n * 3) throw new ArgumentException("Images must be the same size.");
		long match = 0, absSum = 0;
		for (long i = 0; i < n; i++)
		{
			int o = (int)(i * 3); int dr = Math.Abs(a[o] - b[o]), dg = Math.Abs(a[o + 1] - b[o + 1]), db = Math.Abs(a[o + 2] - b[o + 2]);
			if (dr + dg + db == 0) match++; absSum += dr + dg + db;
		}
		return new(width, height, n, match, n == 0 ? 100 : 100.0 * match / n, n == 0 ? 0 : absSum / (3.0 * n), Ssim(width, height, a, b), n - match);
	}

	/// <summary>
	/// Mean SSIM on BT.601 luma over 8x8 windows with stride 4 (Wang et al. 2004 constants, L=255). Images smaller
	/// than one window use a single window covering the whole image. Identical images score exactly 1.
	/// </summary>
	public static double Ssim(int width, int height, ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
	{
		const double C1 = 6.5025, C2 = 58.5225; // (0.01*255)^2, (0.03*255)^2
		var la = Luma(width, height, a); var lb = Luma(width, height, b);
		int win = Math.Min(8, Math.Min(width, height)), stride = Math.Max(1, win / 2);
		double total = 0; long windows = 0;
		for (int y = 0; y + win <= height; y += stride)
			for (int x = 0; x + win <= width; x += stride)
			{
				double sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0; int m = win * win;
				for (int yy = y; yy < y + win; yy++)
					for (int xx = x; xx < x + win; xx++)
					{
						double p = la[yy * width + xx], q = lb[yy * width + xx];
						sa += p; sb += q; saa += p * p; sbb += q * q; sab += p * q;
					}
				double ma = sa / m, mb = sb / m, va = saa / m - ma * ma, vb = sbb / m - mb * mb, cov = sab / m - ma * mb;
				total += (2 * ma * mb + C1) * (2 * cov + C2) / ((ma * ma + mb * mb + C1) * (va + vb + C2)); windows++;
			}
		return windows == 0 ? 1 : total / windows;
	}

	static double[] Luma(int width, int height, ReadOnlySpan<byte> rgb)
	{
		var y = new double[width * height];
		for (int i = 0; i < y.Length; i++) y[i] = 0.299 * rgb[i * 3] + 0.587 * rgb[i * 3 + 1] + 0.114 * rgb[i * 3 + 2];
		return y;
	}
}

/// <summary>Minimal PNG reader for comparing captures: 8-bit greyscale, RGB, RGBA, grey+alpha and palette images, no interlace. Alpha is dropped.</summary>
public static class PngReader
{
	public const int MaxDimension = 16384;
	public static (int Width, int Height, byte[] Rgb) DecodeRgb(ReadOnlySpan<byte> data)
	{
		AssetBinary.Require(data.Length > 8 && data[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }), "Not a PNG file.");
		int width = 0, height = 0, colorType = -1; byte[]? plte = null; using var idat = new MemoryStream();
		int pos = 8;
		while (pos + 12 <= data.Length)
		{
			int length = BinaryPrimitives.ReadInt32BigEndian(data[pos..]); string type = System.Text.Encoding.ASCII.GetString(data.Slice(pos + 4, 4));
			AssetBinary.Require(length >= 0 && pos + 12L + length <= data.Length, "Truncated PNG chunk.");
			var body = data.Slice(pos + 8, length);
			if (type == "IHDR")
			{
				width = BinaryPrimitives.ReadInt32BigEndian(body); height = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
				AssetBinary.Require(body[8] == 8 && body[10] == 0 && body[11] == 0 && body[12] == 0, "Only 8-bit, non-interlaced PNGs are supported.");
				colorType = body[9];
				AssetBinary.Require(width is >= 1 and <= MaxDimension && height is >= 1 and <= MaxDimension, "PNG dimensions out of range.");
			}
			else if (type == "PLTE") plte = body.ToArray();
			else if (type == "IDAT") idat.Write(body);
			else if (type == "IEND") break;
			pos += 12 + length;
		}
		int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new InvalidDataException("Unsupported PNG colour type.") };
		AssetBinary.Require(colorType != 3 || plte is { Length: > 0 }, "Palette PNG without PLTE.");
		int stride = width * channels; var raw = new byte[(long)height * stride];
		idat.Position = 0;
		using (var z = new ZLibStream(idat, CompressionMode.Decompress))
		{
			var prev = new byte[stride]; var line = new byte[stride]; Span<byte> filter = stackalloc byte[1];
			for (int y = 0; y < height; y++)
			{
				AssetBinary.Require(z.Read(filter) == 1, "Truncated PNG data."); z.ReadExactly(line);
				for (int x = 0; x < stride; x++)
				{
					int left = x >= channels ? line[x - channels] : 0, up = prev[x], ul = x >= channels ? prev[x - channels] : 0;
					line[x] = (byte)(line[x] + filter[0] switch
					{
						0 => 0, 1 => left, 2 => up, 3 => (left + up) / 2,
						4 => Paeth(left, up, ul),
						_ => throw new InvalidDataException("Bad PNG filter."),
					});
				}
				line.CopyTo(raw, (long)y * stride); (prev, line) = (line, prev);
			}
		}
		var rgb = new byte[width * height * 3];
		for (int i = 0; i < width * height; i++)
		{
			int o = i * channels;
			switch (colorType)
			{
				case 0: case 4: rgb[i * 3] = rgb[i * 3 + 1] = rgb[i * 3 + 2] = raw[o]; break;
				case 3: AssetBinary.Require(raw[o] * 3 + 2 < plte!.Length, "PNG palette index out of range."); Array.Copy(plte, raw[o] * 3, rgb, i * 3, 3); break;
				default: rgb[i * 3] = raw[o]; rgb[i * 3 + 1] = raw[o + 1]; rgb[i * 3 + 2] = raw[o + 2]; break;
			}
		}
		return (width, height, rgb);
	}

	static int Paeth(int a, int b, int c)
	{
		int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
		return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
	}
}
