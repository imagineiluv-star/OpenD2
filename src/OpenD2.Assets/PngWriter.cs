using System.Buffers.Binary;
using System.IO.Compression;

namespace OpenD2.Assets;

// Minimal RGBA8 PNG encoder for the converted cache (no third-party dependency).
public static class PngWriter
{
	private static readonly uint[] Crc = BuildCrc();
	public static byte[] EncodeRgba(int width, int height, ReadOnlySpan<byte> rgba)
	{
		if (width is < 1 or > 16384 || height is < 1 or > 16384 || rgba.Length != width * height * 4) throw new ArgumentException("Invalid RGBA image.");
		using var output = new MemoryStream();
		output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
		var header = new byte[13];
		BinaryPrimitives.WriteInt32BigEndian(header, width); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
		header[8] = 8; header[9] = 6; // 8-bit RGBA
		Chunk(output, "IHDR", header);
		using (var raw = new MemoryStream())
		{
			using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
				for (int y = 0; y < height; y++) { z.WriteByte(0); z.Write(rgba.Slice(y * width * 4, width * 4)); }
			Chunk(output, "IDAT", raw.ToArray());
		}
		Chunk(output, "IEND", []);
		return output.ToArray();
	}
	/// <summary>Converts palette indices to RGBA; index 0 is transparent, as in DC6/DCC.</summary>
	public static byte[] EncodeIndexed(int width, int height, ReadOnlySpan<byte> indices, Palette palette, ReadOnlySpan<byte> opacity = default)
	{
		byte[] alpha = new byte[indices.Length];
		for (int i = 0; i < alpha.Length; i++) alpha[i] = opacity.IsEmpty ? (indices[i] == 0 ? (byte)0 : (byte)255) : opacity[i];
		return EncodeRgba(width, height, palette.ToRgba(indices, alpha));
	}
	private static void Chunk(Stream output, string type, ReadOnlySpan<byte> data)
	{
		Span<byte> word = stackalloc byte[4];
		BinaryPrimitives.WriteInt32BigEndian(word, data.Length); output.Write(word);
		var body = new byte[4 + data.Length];
		System.Text.Encoding.ASCII.GetBytes(type, body); data.CopyTo(body.AsSpan(4));
		output.Write(body);
		BinaryPrimitives.WriteUInt32BigEndian(word, Crc32(body)); output.Write(word);
	}
	internal static uint Crc32(ReadOnlySpan<byte> data)
	{
		uint c = 0xFFFFFFFF;
		foreach (byte b in data) c = Crc[(c ^ b) & 0xFF] ^ (c >> 8);
		return c ^ 0xFFFFFFFF;
	}
	private static uint[] BuildCrc()
	{
		var table = new uint[256];
		for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; table[n] = c; }
		return table;
	}
}
