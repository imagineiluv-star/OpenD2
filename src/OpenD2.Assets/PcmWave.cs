using System.Buffers.Binary;

namespace OpenD2.Assets;

// RIFF/WAVE PCM only. Keep the MPQ source intact; convert unsigned 8-bit samples to signed PCM16.
public sealed class PcmWave
{
	public const int MaxAuditInputBytes = 64 * 1024 * 1024;
	public const int MaxDecodedBytes = 32 * 1024 * 1024;
	public int SampleRate { get; }
	public int Channels { get; }
	public int Frames => Samples.Length / (Channels * 2);
	public ReadOnlyMemory<byte> Samples { get; }
	private PcmWave(int rate, int channels, byte[] samples) { SampleRate = rate; Channels = channels; Samples = samples; }
	// Audit validates PCM structure without allocating a second, decoded sample buffer.
	// Runtime Parse retains its original input/output memory limits.
	public static void Validate(ReadOnlySpan<byte> data) => Inspect(data, MaxAuditInputBytes);
	private static (int Rate, int Channels, int Bits, int Offset, int Length) Inspect(ReadOnlySpan<byte> data, int maxInput)
	{
		AssetBinary.Require(data.Length >= 44 && data.Length <= maxInput && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WAVE"u8), "Expected bounded RIFF/WAVE input.");
		AssetBinary.Require(AssetBinary.U32(data, 4) == data.Length - 8, "RIFF length does not match input.");
		ReadOnlySpan<byte> format = default, samples = default; int cursor = 12, chunks = 0, sampleOffset = 0;
		while (cursor < data.Length)
		{
			AssetBinary.Require(++chunks <= 1024 && data.Length - cursor >= 8, "Invalid WAV chunk table.");
			var id = data.Slice(cursor, 4); uint length = AssetBinary.U32(data, cursor + 4);
			var chunk = AssetBinary.Slice(data, cursor + 8L, length);
			if (id.SequenceEqual("fmt "u8)) { AssetBinary.Require(format.IsEmpty && !chunk.IsEmpty, "Duplicate/empty WAV format."); format = chunk; }
			if (id.SequenceEqual("data"u8)) { AssetBinary.Require(samples.IsEmpty && !chunk.IsEmpty, "Duplicate/empty WAV samples."); samples = chunk; sampleOffset = cursor + 8; }
			long next = cursor + 8L + length + (length & 1);
			AssetBinary.Require(next <= data.Length, "Missing WAV chunk padding."); cursor = (int)next;
		}
		AssetBinary.Require(format.Length is 16 or 18 && !samples.IsEmpty, "Missing/unsupported WAV format or samples.");
		int codec = AssetBinary.U16(format, 0), channels = AssetBinary.U16(format, 2), bits = AssetBinary.U16(format, 14);
		uint rate = AssetBinary.U32(format, 4);
		AssetBinary.Require(codec == 1 && channels is 1 or 2 && bits is 8 or 16 && rate is >= 8000 and <= 96000 && (format.Length == 16 || AssetBinary.U16(format, 16) == 0), "Only PCM8/PCM16 mono/stereo WAV at 8–96 kHz is supported.");
		int alignment = channels * bits / 8;
		AssetBinary.Require(AssetBinary.U16(format, 12) == alignment && AssetBinary.U32(format, 8) == rate * alignment && samples.Length % alignment == 0, "Inconsistent WAV sample alignment/rate.");
		return ((int)rate, channels, bits, sampleOffset, samples.Length);
	}
	public static PcmWave Parse(ReadOnlySpan<byte> data)
	{
		var info = Inspect(data, AssetDecoders.MaxInputBytes);
		var samples = data.Slice(info.Offset, info.Length);
		int bits = info.Bits;
		long decoded = (long)samples.Length * (16 / bits);
		AssetBinary.Require(decoded <= MaxDecodedBytes, "Decoded WAV exceeds 32 MiB.");
		var pcm = new byte[(int)decoded];
		if (bits == 16) samples.CopyTo(pcm);
		else for (int i = 0; i < samples.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)((samples[i] - 128) << 8));
		return new(info.Rate, info.Channels, pcm);
	}
}
