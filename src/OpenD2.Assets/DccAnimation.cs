namespace OpenD2.Assets;

// LSB-first streams, cell palettes first, pixel selectors second (Engine/DCC.cpp).
// Each decoder owns its input; a direction is independent and has bounded working memory.
public sealed record IndexedFrame(int Width, int Height, int Left, int Top, byte[] Indices);
public sealed class DccAnimation
{
	public const int MaxPixels = 16777216;
	private static readonly int[] BitWidths = [0, 1, 2, 4, 6, 8, 10, 12, 14, 16, 20, 24, 26, 28, 30, 32];
	private readonly byte[] data;
	private readonly int[] offsets;
	public int Directions => offsets.Length - 1;
	public int FramesPerDirection { get; }
	private DccAnimation(byte[] data, int[] offsets, int frames)
	{ this.data = data; this.offsets = offsets; FramesPerDirection = frames; }
	public static DccAnimation Parse(ReadOnlySpan<byte> data)
	{
		AssetBinary.Require(data.Length is >= 19 and <= AssetDecoders.MaxInputBytes, "DCC input exceeds size bounds.");
		AssetBinary.Require(data[0] == 0x74 && data[1] == 6 && AssetBinary.U32(data, 7) == 1, "Unsupported DCC signature, version or tag.");
		int directions = data[2]; uint frames = AssetBinary.U32(data, 3);
		AssetBinary.Require(directions is >= 1 and <= 32 && frames is >= 1 and <= 4096 && frames * directions <= 4096, "DCC frame count exceeds budget.");
		int headerEnd = 15 + directions * 4;
		var offsets = new int[directions + 1]; offsets[^1] = data.Length;
		for (int i = 0; i < directions; i++)
		{
			uint offset = AssetBinary.U32(data, 15 + i * 4);
			AssetBinary.Require(offset >= headerEnd && offset < data.Length && (i == 0 || offset > offsets[i - 1]), "Invalid DCC direction offset.");
			offsets[i] = (int)offset;
		}
		return new DccAnimation(data.ToArray(), offsets, (int)frames);
	}
	public IReadOnlyList<IndexedFrame> DecodeDirection(int direction)
	{
		if ((uint)direction >= Directions) throw new ArgumentOutOfRangeException(nameof(direction));
		var bits = new Bits(data, offsets[direction] * 8, offsets[direction + 1] * 8);
		bits.Read(32); int flags = (int)bits.Read(2);
		var widths = new int[7]; for (int i = 0; i < 7; i++) widths[i] = BitWidths[bits.Read(4)];
		var headers = new Header[FramesPerDirection];
		long optionalBytes = 0, totalPixels = 0, minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
		for (int f = 0; f < headers.Length; f++)
		{
			bits.Read(widths[0]); uint w = bits.Read(widths[1]), h = bits.Read(widths[2]);
			int x = bits.Signed(widths[3]), y = bits.Signed(widths[4]);
			optionalBytes += bits.Read(widths[5]); bits.Read(widths[6]);
			AssetBinary.Require(bits.Read(1) == 0, "Bottom-up DCC frames are not supported yet.");
			AssetBinary.Require(w is >= 1 and <= 4096 && h is >= 1 and <= 4096, "Invalid DCC frame dimensions.");
			long top = (long)y - h + 1, right = (long)x + w, bottom = (long)y + 1;
			AssetBinary.Require(top >= int.MinValue && right <= int.MaxValue && bottom <= int.MaxValue, "DCC coordinate overflow.");
			totalPixels += (long)w * h;
			AssetBinary.Require(totalPixels <= MaxPixels && optionalBytes <= AssetDecoders.MaxInputBytes, "DCC decoded pixel or optional data budget exceeded.");
			headers[f] = new Header((int)w, (int)h, x, (int)top);
			minX = Math.Min(minX, x); minY = Math.Min(minY, top); maxX = Math.Max(maxX, right); maxY = Math.Max(maxY, bottom);
		}
		long canvasW = maxX - minX, canvasH = maxY - minY;
		AssetBinary.Require(canvasW <= 4096 && canvasH <= 4096 && canvasW * canvasH <= MaxPixels, "DCC direction canvas exceeds budget.");
		if (optionalBytes != 0) { bits.Align(); bits.Take((int)optionalBytes * 8); }
		int equalSize = (flags & 2) != 0 ? (int)bits.Read(20) : 0, maskSize = (int)bits.Read(20);
		int encodingSize = (flags & 1) != 0 ? (int)bits.Read(20) : 0, rawSize = (flags & 1) != 0 ? (int)bits.Read(20) : 0;
		var palette = new List<byte>(); for (int i = 0; i < 256; i++) if (bits.Read(1) != 0) palette.Add((byte)i);
		AssetBinary.Require(palette.Count > 0, "DCC has no palette entries.");
		var equal = bits.Take(equalSize); var masks = bits.Take(maskSize); var encoding = bits.Take(encodingSize); var raw = bits.Take(rawSize);
		int cw = (int)canvasW, ch = (int)canvasH, cellsWide = (cw + 3) / 4, cellCount = cellsWide * ((ch + 3) / 4);
		var known = new bool[cellCount]; var previous = new uint[cellCount];
		var cells = new List<Cell>[headers.Length]; int totalCells = 0;
		Span<int> values = stackalloc int[4];
		for (int f = 0; f < headers.Length; f++)
		{
			var header = headers[f]; var frameCells = new List<Cell>(); cells[f] = frameCells;
			int startX = header.X - (int)minX, startY = header.Y - (int)minY;
			foreach (var (y, h) in Segments(startY, header.H))
			foreach (var (x, w) in Segments(startX, header.W))
			{
				AssetBinary.Require(++totalCells <= 300000, "DCC cell budget exceeded.");
				int slot = x / 4 + y / 4 * cellsWide;
				bool reuse = known[slot] && equalSize != 0 && equal.Read(1) != 0;
				uint colors = previous[slot];
				if (!reuse)
				{
					int mask = known[slot] ? (int)masks.Read(4) : 15;
					int count = System.Numerics.BitOperations.PopCount((uint)mask), decoded = 0, last = 0;
					bool direct = count != 0 && encodingSize != 0 && encoding.Read(1) != 0;
					for (int i = 0; i < count; i++)
					{
						int value = direct ? (int)raw.Read(8) : last;
						if (!direct)
						{
							uint delta;
							do { delta = bits.Read(4); value += (int)delta; AssetBinary.Require(value < palette.Count, "DCC palette displacement out of range."); } while (delta == 15);
						}
						if (value == last) break;
						AssetBinary.Require(value < palette.Count, "DCC raw palette code out of range.");
						values[decoded++] = value; last = value;
					}
					for (int i = 0; i < 4; i++) if ((mask & (1 << i)) != 0)
					{
						int value = decoded > 0 ? values[--decoded] : 0;
						colors = (colors & ~(255u << (8 * i))) | ((uint)value << (8 * i));
					}
					known[slot] = true; previous[slot] = colors;
				}
				uint mapped = 0;
				for (int i = 0; i < 4; i++) mapped |= (uint)palette[(int)((colors >> (i * 8)) & 255)] << (i * 8);
				frameCells.Add(new Cell(x, y, w, h, slot, mapped, reuse));
			}
		}
		// The remaining code stream now contains pixel selectors for every changed cell.
		var canvas = new byte[cw * ch]; var lastCells = new Cell[cellCount]; var frames = new IndexedFrame[headers.Length];
		Span<byte> copy = stackalloc byte[25]; // cells may merge a final 1-pixel strip: at most 5 x 5
		for (int f = 0; f < frames.Length; f++)
		{
			var header = headers[f]; var pixels = new byte[header.W * header.H];
			foreach (var cell in cells[f])
			{
				var old = lastCells[cell.Slot];
				if (cell.Reuse)
				{
					copy.Clear();
					if (old.W == cell.W && old.H == cell.H)
						for (int y = 0; y < cell.H; y++) canvas.AsSpan((old.Y + y) * cw + old.X, cell.W).CopyTo(copy.Slice(y * cell.W, cell.W));
					for (int y = 0; y < cell.H; y++) copy.Slice(y * cell.W, cell.W).CopyTo(canvas.AsSpan((cell.Y + y) * cw + cell.X, cell.W));
				}
				else
				{
					byte a = (byte)cell.Colors, b = (byte)(cell.Colors >> 8), c = (byte)(cell.Colors >> 16);
					int n = a == b ? 0 : b == c ? 1 : 2;
					for (int y = 0; y < cell.H; y++) for (int x = 0; x < cell.W; x++)
						canvas[(cell.Y + y) * cw + cell.X + x] = (byte)(cell.Colors >> ((int)bits.Read(n) * 8));
				}
				int fx = cell.X - (header.X - (int)minX), fy = cell.Y - (header.Y - (int)minY);
				for (int y = 0; y < cell.H; y++) canvas.AsSpan((cell.Y + y) * cw + cell.X, cell.W).CopyTo(pixels.AsSpan((fy + y) * header.W + fx, cell.W));
				lastCells[cell.Slot] = cell;
			}
			frames[f] = new IndexedFrame(header.W, header.H, header.X, header.Y, pixels);
		}
		AssetBinary.Require(equal.Remaining == 0 && masks.Remaining == 0 && encoding.Remaining == 0 && raw.Remaining == 0 && bits.Remaining <= 7, "DCC stream consumption mismatch.");
		return Array.AsReadOnly(frames);
	}
	private static IEnumerable<(int Start, int Length)> Segments(int start, int length)
	{
		int size = 4 - start % 4;
		while (length > size + 1) { yield return (start, size); start += size; length -= size; size = 4; }
		yield return (start, length);
	}
	private readonly record struct Header(int W, int H, int X, int Y);
	private readonly record struct Cell(int X, int Y, int W, int H, int Slot, uint Colors, bool Reuse);
	private sealed class Bits(byte[] data, int position, int end)
	{
		public int Remaining => end - position;
		public uint Read(int count)
		{
			AssetBinary.Require(count is >= 0 and <= 32 && count <= Remaining, "Truncated DCC bitstream.");
			uint value = 0;
			for (int i = 0; i < count; i++, position++) value |= (uint)((data[position / 8] >> (position % 8)) & 1) << i;
			return value;
		}
		public int Signed(int count)
		{
			uint value = Read(count);
			if (count is > 0 and < 32 && (value & (1u << (count - 1))) != 0) value |= uint.MaxValue << count;
			return unchecked((int)value);
		}
		public Bits Take(int count)
		{
			AssetBinary.Require(count >= 0 && count <= Remaining, "DCC substream exceeds direction.");
			var result = new Bits(data, position, position + count); position += count; return result;
		}
		public void Align() { Take((8 - position % 8) % 8); }
	}
}
