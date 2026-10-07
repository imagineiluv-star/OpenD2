using System.Buffers.Binary;
using System.Text;

namespace OpenD2.Assets;

// Format layouts: Engine/Palette.cpp, DC6.hpp and TBL_Text.hpp.
// Limits are deliberate policy, not claims about every possible modded asset.
internal static class AssetBinary
{
	internal static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> data, long offset, long length)
	{
		if (offset < 0 || length < 0 || offset > data.Length || length > data.Length - offset)
			throw new InvalidDataException("Asset range exceeds input.");
		return data.Slice((int)offset, (int)length);
	}
	internal static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(Slice(data, offset, 4));
	internal static ushort U16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(Slice(data, offset, 2));
	internal static void Require(bool condition, string message)
	{
		if (!condition) throw new InvalidDataException(message);
	}
}

public sealed class Palette
{
	private readonly byte[] rgb;
	private Palette(byte[] rgb) { this.rgb = rgb; }
	public static Palette Parse(ReadOnlySpan<byte> data)
	{
		AssetBinary.Require(data.Length == 768, "pal.dat must contain exactly 256 BGR colors.");
		var rgb = new byte[768];
		for (int i = 0; i < 256; i++)
		{
			rgb[i * 3] = data[i * 3 + 2]; rgb[i * 3 + 1] = data[i * 3 + 1]; rgb[i * 3 + 2] = data[i * 3];
		}
		return new Palette(rgb);
	}
	public byte[] ToRgba(ReadOnlySpan<byte> indices, ReadOnlySpan<byte> opacity)
	{
		AssetBinary.Require(indices.Length == opacity.Length && indices.Length <= 16777216, "Invalid pixel planes or pixel budget.");
		var pixels = new byte[indices.Length * 4];
		for (int i = 0; i < indices.Length; i++)
		{
			rgb.AsSpan(indices[i] * 3, 3).CopyTo(pixels.AsSpan(i * 4, 3)); pixels[i * 4 + 3] = opacity[i];
		}
		return pixels;
	}
}

public sealed record Dc6Frame(int Width, int Height, int OffsetX, int OffsetY, byte[] Indices, byte[] Opacity);

public sealed class Dc6Image
{
	private readonly byte[] data;
	private readonly int[] offsets;
	public int Directions { get; }
	public int FramesPerDirection { get; }
	public int FrameCount => offsets.Length;
	private Dc6Image(byte[] data, int[] offsets, int directions, int frames)
	{ this.data = data; this.offsets = offsets; Directions = directions; FramesPerDirection = frames; }
	public static Dc6Image Parse(ReadOnlySpan<byte> data)
	{
		AssetBinary.Require(data.Length is >= 24 and <= 33554432, "DC6 input exceeds size bounds.");
		AssetBinary.Require(AssetBinary.U32(data, 0) == 6, "Unsupported DC6 version.");
		uint directions = AssetBinary.U32(data, 16), frames = AssetBinary.U32(data, 20);
		AssetBinary.Require(directions is >= 1 and <= 32 && frames is >= 1 and <= 4096 && (ulong)directions * frames <= 4096, "DC6 frame count exceeds budget.");
		int count = (int)(directions * frames), tableEnd = 24 + count * 4;
		AssetBinary.Slice(data, 24, count * 4);
		var offsets = new int[count]; long pixels = 0;
		var ranges = new List<(long Start, long End)>();
		for (int i = 0; i < count; i++)
		{
			uint position = AssetBinary.U32(data, 24 + i * 4);
			AssetBinary.Require(position >= tableEnd, "DC6 frame overlaps header.");
			var header = AssetBinary.Slice(data, position, 32);
			uint width = AssetBinary.U32(header, 4), height = AssetBinary.U32(header, 8), length = AssetBinary.U32(header, 28);
			AssetBinary.Require(AssetBinary.U32(header, 0) <= 1 && width is >= 1 and <= 4096 && height is >= 1 and <= 4096, "Invalid DC6 dimensions or flip.");
			pixels += (long)width * height;
			AssetBinary.Require(pixels <= 16777216, "DC6 total pixel budget exceeded.");
			AssetBinary.Slice(data, (long)position + 32, length);
			offsets[i] = (int)position; ranges.Add((position, (long)position + 32 + length));
		}
		ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
		for (int i = 1; i < ranges.Count; i++) AssetBinary.Require(ranges[i].Start >= ranges[i - 1].End, "Overlapping DC6 frames.");
		return new Dc6Image(data.ToArray(), offsets, (int)directions, (int)frames);
	}
	// Decode one frame at a time; retain offsets for later animation/atlas work.
	public Dc6Frame DecodeFrame(int direction, int frame)
	{
		if ((uint)direction >= Directions || (uint)frame >= FramesPerDirection) throw new ArgumentOutOfRangeException(nameof(frame));
		int position = offsets[direction * FramesPerDirection + frame];
		var header = data.AsSpan(position, 32);
		int width = (int)AssetBinary.U32(header, 4), height = (int)AssetBinary.U32(header, 8);
		var encoded = AssetBinary.Slice(data, position + 32L, AssetBinary.U32(header, 28));
		var indices = new byte[width * height]; var opacity = new byte[indices.Length];
		bool topDown = AssetBinary.U32(header, 0) == 1;
		int x = 0, row = 0, cursor = 0;
		while (cursor < encoded.Length)
		{
			AssetBinary.Require(row < height, "DC6 data after final row.");
			byte code = encoded[cursor++];
			if (code == 0x80) { row++; x = 0; continue; }
			int run = code & 0x7f;
			AssetBinary.Require(run > 0 && run <= width - x, "DC6 run exceeds row.");
			if ((code & 0x80) == 0)
			{
				int target = (topDown ? row : height - 1 - row) * width + x;
				AssetBinary.Slice(encoded, cursor, run).CopyTo(indices.AsSpan(target, run));
				opacity.AsSpan(target, run).Fill(255); cursor += run;
			}
			x += run;
		}
		AssetBinary.Require(row == height, "DC6 missing row terminator.");
		return new Dc6Frame(width, height, unchecked((int)AssetBinary.U32(header, 12)), unchecked((int)AssetBinary.U32(header, 16)), indices, opacity);
	}
}

// Text TBL only. Preserve original bytes; callers choose the language encoding explicitly.
public sealed record TblEntry(int Index, ReadOnlyMemory<byte> KeyBytes, ReadOnlyMemory<byte> ValueBytes)
{
	public string Key(Encoding encoding) => encoding.GetString(KeyBytes.Span);
	public string Value(Encoding encoding) => encoding.GetString(ValueBytes.Span);
}
public sealed class StringTable
{
	public ushort StoredCrc { get; }
	public IReadOnlyList<TblEntry> Entries { get; }
	private StringTable(ushort crc, TblEntry[] entries) { StoredCrc = crc; Entries = Array.AsReadOnly(entries); }
	public static StringTable Parse(ReadOnlySpan<byte> data)
	{
		AssetBinary.Require(data.Length is >= 21 and <= 33554432, "TBL input exceeds size bounds.");
		int nodes = AssetBinary.U16(data, 2); uint slots = AssetBinary.U32(data, 4), start = AssetBinary.U32(data, 9);
		AssetBinary.Require(data[8] <= 1 && slots <= 100000 && AssetBinary.U32(data, 17) == data.Length, "Invalid TBL version, slots or declared size.");
		long hashStart = 21L + nodes * 2, hashEnd = hashStart + slots * 17L;
		AssetBinary.Require(start >= hashEnd && start <= data.Length, "Invalid TBL data start.");
		AssetBinary.Slice(data, 21, hashEnd - 21);
		for (int i = 0; i < nodes; i++) AssetBinary.Require(AssetBinary.U16(data, 21 + i * 2) < slots, "TBL index outside hash table.");
		byte[] owned = data.ToArray(); var result = new List<TblEntry>(); long stringBytes = 0;
		for (int i = 0; i < slots; i++)
		{
			var node = AssetBinary.Slice(data, hashStart + i * 17L, 17);
			if (node[0] == 0) continue;
			int index = AssetBinary.U16(node, 1); uint key = AssetBinary.U32(node, 7), value = AssetBinary.U32(node, 11);
			int length = AssetBinary.U16(node, 15);
			AssetBinary.Require(index < nodes && AssetBinary.U16(data, 21 + index * 2) == i, "TBL active index mismatch.");
			AssetBinary.Require(key >= start && value >= start && length > 0, "Invalid TBL string range.");
			var text = AssetBinary.Slice(data, value, length);
			AssetBinary.Require(text[^1] == 0, "TBL value missing terminator.");
			var keyTail = AssetBinary.Slice(data, key, Math.Min(65536L, data.Length - (long)key));
			int keyLength = keyTail.IndexOf((byte)0);
			AssetBinary.Require(keyLength >= 0, "TBL key missing terminator.");
			stringBytes += keyLength + (long)length;
			AssetBinary.Require(stringBytes <= 33554432, "TBL total string budget exceeded.");
			result.Add(new TblEntry(index, owned.AsMemory((int)key, keyLength), owned.AsMemory((int)value, length - 1)));
		}
		return new StringTable(AssetBinary.U16(data, 0), result.OrderBy(e => e.Index).ToArray());
	}
}
