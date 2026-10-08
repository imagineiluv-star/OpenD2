using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace OpenD2.Assets;

public sealed record LegacySavePreview(uint Version, string Name, byte ClassId, byte Level, bool Expansion,
	bool Hardcore, bool Dead, int FileBytes, string Sha256, bool ChecksumValid = true, bool BodyValidated = false, bool ImportSupported = false);

// Read-only v96 header preflight, not a character/items/quest importer.
// Layout: Shared/D2Common_Shared.hpp; checksum cross-checked against dschu012/d2s header.ts.
public static class LegacySaveInspector
{
	public const int HeaderBytes = 335, MaxBytes = 1024 * 1024;
	public static LegacySavePreview Read(string path)
	{
		using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
		if (stream.Length is < HeaderBytes or > MaxBytes) throw new InvalidDataException("Legacy save size is outside the inspection budget.");
		var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes); return Parse(bytes);
	}
	public static LegacySavePreview Parse(ReadOnlySpan<byte> data)
	{
		if (data.Length is < HeaderBytes or > MaxBytes) throw new InvalidDataException("Truncated or oversized legacy save.");
		if (BinaryPrimitives.ReadUInt32LittleEndian(data) != 0xaa55aa55) throw new InvalidDataException("Not a legacy D2S file.");
		uint version = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
		if (version != 96) throw new InvalidDataException("Only legacy save version 96 header inspection is supported; D2R import is not supported.");
		if (BinaryPrimitives.ReadUInt32LittleEndian(data[8..]) != data.Length) throw new InvalidDataException("Legacy save length mismatch.");
		uint checksum = 0;
		for (int i = 0; i < data.Length; i++) checksum = unchecked((checksum << 1) + (checksum >> 31) + (i is >= 12 and < 16 ? 0u : data[i]));
		if (checksum != BinaryPrimitives.ReadUInt32LittleEndian(data[12..])) throw new InvalidDataException("Legacy save checksum mismatch.");
		var name = data.Slice(20, 16); int end = name.IndexOf((byte)0);
		if (end is < 1 or > 15) throw new InvalidDataException("Invalid legacy name terminator.");
		foreach (byte c in name[..end]) if (c is < 32 or > 126) throw new InvalidDataException("Non-ASCII legacy names require an explicit encoding profile.");
		byte classId = data[40], level = data[43], status = data[36];
		if (classId > 6 || level is < 1 or > 99) throw new InvalidDataException("Invalid legacy class or level.");
		return new(version, Encoding.ASCII.GetString(name[..end]), classId, level, (status & 32) != 0, (status & 4) != 0, (status & 8) != 0,
			data.Length, Convert.ToHexStringLower(SHA256.HashData(data)));
	}
}
