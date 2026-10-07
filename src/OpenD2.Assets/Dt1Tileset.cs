namespace OpenD2.Assets;

public readonly record struct TileKey(int MainIndex, int SubIndex, int Orientation);

public sealed class Dt1Tile
{
	public TileKey Key { get; }
	public int Direction { get; }
	public short RoofHeight { get; }
	public ushort MaterialFlags { get; }
	public int DeclaredWidth { get; }
	public int DeclaredHeight { get; }
	public int RarityOrFrame { get; }
	public ReadOnlyMemory<byte> RawSubtileFlags { get; }
	internal Dt1Block[] Blocks { get; }
	internal int Left { get; }
	internal int Top { get; }
	public int PixelWidth { get; }
	public int PixelHeight { get; }
	internal Dt1Tile(ReadOnlySpan<byte> header, Dt1Block[] blocks)
	{
		Key = new(Signed(header, 24), Signed(header, 28), Signed(header, 20));
		Direction = Signed(header, 0); RoofHeight = unchecked((short)AssetBinary.U16(header, 4));
		MaterialFlags = AssetBinary.U16(header, 6); DeclaredHeight = Signed(header, 8); DeclaredWidth = Signed(header, 12);
		RarityOrFrame = Signed(header, 32); RawSubtileFlags = header.Slice(40, 25).ToArray(); Blocks = blocks;
		// Keep signed block coordinates. Never modify the on-disk tile height during decoding.
		Left = blocks.Length == 0 ? 0 : blocks.Min(b => b.X);
		Top = blocks.Length == 0 ? 0 : blocks.Min(b => b.Y);
		PixelWidth = blocks.Length == 0 ? 1 : blocks.Max(b => b.X + 32) - Left;
		PixelHeight = blocks.Length == 0 ? 1 : blocks.Max(b => b.Y + (b.Format == 1 ? 15 : 32)) - Top;
		AssetBinary.Require(PixelWidth <= 4096 && PixelHeight <= 4096, "DT1 tile canvas exceeds budget.");
	}
	// File rows run from bottom to top; callers use x/y in a top-to-bottom 5 x 5 grid.
	public byte CollisionAt(int x, int y)
	{
		if ((uint)x >= 5 || (uint)y >= 5) throw new ArgumentOutOfRangeException(nameof(x));
		return RawSubtileFlags.Span[(4 - y) * 5 + x];
	}
	internal static int Signed(ReadOnlySpan<byte> data, int offset) => unchecked((int)AssetBinary.U32(data, offset));
}

internal readonly record struct Dt1Block(short X, short Y, ushort Format, int Offset, int Length);

public sealed class Dt1Tileset
{
	public const int MaxPixels = 16777216;
	private readonly byte[] data;
	public IReadOnlyList<Dt1Tile> Tiles { get; }
	private Dt1Tileset(byte[] data, Dt1Tile[] tiles) { this.data = data; Tiles = Array.AsReadOnly(tiles); }
	public static Dt1Tileset Parse(ReadOnlySpan<byte> data)
	{
		AssetBinary.Require(data.Length is >= 276 and <= AssetDecoders.MaxInputBytes, "DT1 input exceeds size bounds.");
		AssetBinary.Require(AssetBinary.U32(data, 0) == 7 && AssetBinary.U32(data, 4) == 6, "Unsupported DT1 version (expected 7.6).");
		uint count = AssetBinary.U32(data, 268), start = AssetBinary.U32(data, 272);
		AssetBinary.Require(count <= 4096 && start >= 276, "Invalid DT1 tile table.");
		AssetBinary.Slice(data, start, count * 96L);
		long headerEnd = start + count * 96L, pixels = 0, blockCount = 0;
		var tiles = new Dt1Tile[count]; var ranges = new List<(long Start, long End)>();
		for (int i = 0; i < tiles.Length; i++)
		{
			var header = AssetBinary.Slice(data, start + i * 96L, 96);
			int width = Dt1Tile.Signed(header, 12), height = Dt1Tile.Signed(header, 8);
			AssetBinary.Require(width is >= 0 and <= 4096 && height is >= -4096 and <= 4096, "Invalid DT1 declared dimensions.");
			uint pointer = AssetBinary.U32(header, 72), length = AssetBinary.U32(header, 76), number = AssetBinary.U32(header, 80);
			blockCount += number;
			AssetBinary.Require(number <= 16384 && blockCount <= 262144, "DT1 block count exceeds budget.");
			AssetBinary.Require(number == 0 || (width > 0 && height != 0), "DT1 nonempty tile has empty dimensions.");
			AssetBinary.Require(pointer >= headerEnd || (length == 0 && number == 0), "DT1 blocks overlap tile headers.");
			var body = AssetBinary.Slice(data, pointer, length);
			AssetBinary.Slice(body, 0, number * 20L);
			if (length > 0) ranges.Add((pointer, (long)pointer + length));
			var blocks = new Dt1Block[number]; var payloads = new List<(long Start, long End)>();
			for (int b = 0; b < blocks.Length; b++)
			{
				var block = body.Slice(b * 20, 20);
				ushort format = AssetBinary.U16(block, 8);
				AssetBinary.Require(format is 1 or 0x1001 or 0x2005, "Unsupported DT1 block encoding.");
				uint size = AssetBinary.U32(block, 10), offset = AssetBinary.U32(block, 16);
				AssetBinary.Require(offset >= number * 20L && size <= 4096, "Invalid DT1 block payload.");
				AssetBinary.Slice(body, offset, size);
				AssetBinary.Require(format != 1 || size == 256, "DT1 isometric block must contain 256 bytes.");
				blocks[b] = new(unchecked((short)AssetBinary.U16(block, 0)), unchecked((short)AssetBinary.U16(block, 2)), format, (int)(pointer + offset), (int)size);
				if (size > 0) payloads.Add((offset, (long)offset + size));
			}
			NoOverlap(payloads);
			tiles[i] = new Dt1Tile(header, blocks); pixels += (long)tiles[i].PixelWidth * tiles[i].PixelHeight;
			AssetBinary.Require(pixels <= MaxPixels, "DT1 decoded pixel budget exceeded.");
		}
		NoOverlap(ranges);
		return new Dt1Tileset(data.ToArray(), tiles);
	}
	private static void NoOverlap(List<(long Start, long End)> ranges)
	{
		ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
		for (int i = 1; i < ranges.Count; i++) AssetBinary.Require(ranges[i].Start >= ranges[i - 1].End, "DT1 overlapping data ranges.");
	}
	public IndexedFrame DecodeTile(int index)
	{
		if ((uint)index >= Tiles.Count) throw new ArgumentOutOfRangeException(nameof(index));
		var tile = Tiles[index]; var pixels = new byte[tile.PixelWidth * tile.PixelHeight];
		foreach (var block in tile.Blocks)
		{
			var encoded = data.AsSpan(block.Offset, block.Length); int cursor = 0;
			if (block.Format == 1)
			{
				for (int y = 0; y < 15; y++)
				{
					int skip = Math.Abs(7 - y) * 2, run = 32 - skip * 2;
					encoded.Slice(cursor, run).CopyTo(pixels.AsSpan((block.Y - tile.Top + y) * tile.PixelWidth + block.X - tile.Left + skip, run));
					cursor += run;
				}
			}
			else
			{
				int x = 0, y = 0;
				while (cursor < encoded.Length)
				{
					AssetBinary.Require(y < 32 && encoded.Length - cursor >= 2, "DT1 truncated RLE pair or too many rows.");
					int skip = encoded[cursor++], run = encoded[cursor++];
					if (skip == 0 && run == 0) { x = 0; y++; continue; }
					AssetBinary.Require(skip <= 32 - x && run <= 32 - x - skip, "DT1 RLE run exceeds block width.");
					x += skip;
					AssetBinary.Slice(encoded, cursor, run).CopyTo(pixels.AsSpan((block.Y - tile.Top + y) * tile.PixelWidth + block.X - tile.Left + x, run));
					cursor += run; x += run;
				}
			}
		}
		return new IndexedFrame(tile.PixelWidth, tile.PixelHeight, tile.Left, tile.Top, pixels);
	}
}
