using System.Text;

namespace OpenD2.Assets;

public sealed record CofLayer(byte Component, byte Shadow, byte Selectable, byte OverrideTransparency, byte DrawEffect, string WeaponClass);
public sealed class CofAnimation
{
	private readonly byte[] order;
	public int Directions { get; }
	public int FramesPerDirection { get; }
	public ushort Speed { get; }
	public IReadOnlyList<CofLayer> Layers { get; }
	public ReadOnlyMemory<byte> Events { get; }
	private CofAnimation(int directions, int frames, ushort speed, CofLayer[] layers, byte[] events, byte[] order)
	{ Directions = directions; FramesPerDirection = frames; Speed = speed; Layers = Array.AsReadOnly(layers); Events = events; this.order = order; }
	public static CofAnimation Parse(ReadOnlySpan<byte> data)
	{
		AssetBinary.Require(data.Length is >= 28 and <= 1048576, "COF input exceeds size bounds.");
		int count = data[0], frames = data[1], directions = data[2];
		AssetBinary.Require(data[3] == 20, "Unsupported COF version.");
		AssetBinary.Require(count is >= 1 and <= 16 && frames >= 1 && directions is >= 1 and <= 32, "Invalid COF dimensions.");
		int eventsAt = 28 + count * 9, orderAt = eventsAt + frames;
		AssetBinary.Require(data.Length == orderAt + directions * frames * count, "COF length does not match layers, events and order table.");
		var layers = new CofLayer[count]; int present = 0;
		for (int i = 0; i < count; i++)
		{
			var layer = data.Slice(28 + i * 9, 9); int component = layer[0];
			AssetBinary.Require(component < 16 && (present & (1 << component)) == 0 && layer[8] == 0, "Invalid COF component or weapon class.");
			present |= 1 << component;
			layers[i] = new CofLayer(layer[0], layer[1], layer[2], layer[3], layer[4], Encoding.ASCII.GetString(layer.Slice(5, 3)).TrimEnd('\0'));
		}
		for (int at = orderAt; at < data.Length; at += count)
		{
			int seen = 0;
			for (int i = 0; i < count; i++)
			{
				int component = data[at + i];
				AssetBinary.Require(component < 16 && (present & (1 << component)) != 0 && (seen & (1 << component)) == 0, "COF order must be a permutation of its components.");
				seen |= 1 << component;
			}
		}
		return new CofAnimation(directions, frames, AssetBinary.U16(data, 24), layers, data.Slice(eventsAt, frames).ToArray(), data[orderAt..].ToArray());
	}
	public ReadOnlySpan<byte> DrawOrder(int direction, int frame)
	{
		if ((uint)direction >= Directions || (uint)frame >= FramesPerDirection) throw new ArgumentOutOfRangeException(nameof(frame));
		return order.AsSpan((direction * FramesPerDirection + frame) * Layers.Count, Layers.Count);
	}
}

// One selected direction, normalized to a stable origin/canvas across all frames.
// DCC index zero is transparent. Exact PL2 blending and shadow generation are separate work.
public sealed class AnimationClip
{
	public int Width { get; }
	public int Height { get; }
	public int Left { get; }
	public int Top { get; }
	public IReadOnlyList<IndexedFrame> Frames { get; }
	private AnimationClip(int width, int height, int left, int top, IndexedFrame[] frames)
	{ Width = width; Height = height; Left = left; Top = top; Frames = Array.AsReadOnly(frames); }
	public static AnimationClip Single(IReadOnlyList<IndexedFrame> frames) => Build(null, 0, new Dictionary<byte, IReadOnlyList<IndexedFrame>> { [0] = frames });
	public static AnimationClip Compose(CofAnimation cof, int direction, IReadOnlyDictionary<byte, IReadOnlyList<IndexedFrame>> layers) => Build(cof, direction, layers);
	private static AnimationClip Build(CofAnimation? cof, int direction, IReadOnlyDictionary<byte, IReadOnlyList<IndexedFrame>> layers)
	{
		AssetBinary.Require(layers.Count is >= 1 and <= 16, "Animation needs 1 to 16 layers.");
		int count = cof?.FramesPerDirection ?? layers.First().Value.Count;
		AssetBinary.Require(count is >= 1 and <= 4096, "Invalid animation frame count.");
		if (cof is not null)
		{
			cof.DrawOrder(direction, 0);
			AssetBinary.Require(layers.Count == cof.Layers.Count && cof.Layers.All(layer => layers.ContainsKey(layer.Component)), "COF layer mapping is incomplete or contains extra components.");
			AssetBinary.Require(cof.Layers.All(layer => layer.OverrideTransparency == 0), "COF transparency effects require a PL2 renderer.");
		}
		long left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue, sourcePixels = 0;
		foreach (var frames in layers.Values)
		{
			AssetBinary.Require(frames.Count == count, "Layer frame count does not match COF.");
			foreach (var f in frames)
			{
				AssetBinary.Require(f.Width is >= 1 and <= 4096 && f.Height is >= 1 and <= 4096 && f.Indices.Length == (long)f.Width * f.Height, "Invalid animation pixel plane.");
				sourcePixels += f.Indices.Length; AssetBinary.Require(sourcePixels <= DccAnimation.MaxPixels, "Animation source pixel budget exceeded.");
				left = Math.Min(left, f.Left); top = Math.Min(top, f.Top); right = Math.Max(right, (long)f.Left + f.Width); bottom = Math.Max(bottom, (long)f.Top + f.Height);
			}
		}
		long width = right - left, height = bottom - top;
		AssetBinary.Require(width <= 4096 && height <= 4096 && width * height * count <= DccAnimation.MaxPixels, "Animation canvas budget exceeded.");
		int w = (int)width, h = (int)height; var result = new IndexedFrame[count];
		byte[] singleOrder = [0];
		for (int f = 0; f < count; f++)
		{
			var pixels = new byte[w * h]; ReadOnlySpan<byte> order = cof is null ? singleOrder : cof.DrawOrder(direction, f);
			foreach (byte component in order)
			{
				var source = layers[component][f]; int dx = (int)(source.Left - left), dy = (int)(source.Top - top);
				for (int y = 0; y < source.Height; y++) for (int x = 0; x < source.Width; x++)
				{
					byte color = source.Indices[y * source.Width + x];
					if (color != 0) pixels[(y + dy) * w + x + dx] = color;
				}
			}
			result[f] = new IndexedFrame(w, h, (int)left, (int)top, pixels);
		}
		return new AnimationClip(w, h, (int)left, (int)top, result);
	}
	public byte[] ToRgba(int frame, Palette palette)
	{
		var pixels = Frames[frame].Indices; var alpha = new byte[pixels.Length];
		for (int i = 0; i < pixels.Length; i++) if (pixels[i] != 0) alpha[i] = 255;
		return palette.ToRgba(pixels, alpha);
	}
	public int FrameAt(double seconds, double framesPerSecond)
	{
		if (!double.IsFinite(seconds) || seconds < 0 || !double.IsFinite(framesPerSecond) || framesPerSecond <= 0 || framesPerSecond > 120) throw new ArgumentOutOfRangeException(nameof(seconds));
		return (int)((seconds % (Frames.Count / framesPerSecond)) * framesPerSecond) % Frames.Count;
	}
}
