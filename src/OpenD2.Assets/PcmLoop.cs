using System.Buffers.Binary;

namespace OpenD2.Assets;

// Independent playback cursor over immutable PCM. No I/O or allocation in ReadStereo.
public sealed class PcmLoop(PcmWave wave)
{
	public int Position { get; private set; }
	public long FramesRead { get; private set; }
	public void Reset() { Position = 0; FramesRead = 0; }
	public void ReadStereo(Span<float> output)
	{
		if (output.Length % 2 != 0 || output.Length > 16384) throw new ArgumentException("Expected at most 8192 stereo frames.", nameof(output));
		var samples = wave.Samples.Span;
		for (int i = 0; i < output.Length; i += 2)
		{
			int offset = Position * wave.Channels * 2;
			float left = BinaryPrimitives.ReadInt16LittleEndian(samples.Slice(offset, 2)) / 32768f;
			float right = wave.Channels == 1 ? left : BinaryPrimitives.ReadInt16LittleEndian(samples.Slice(offset + 2, 2)) / 32768f;
			output[i] = left; output[i + 1] = right;
			if (++Position == wave.Frames) Position = 0;
			FramesRead++;
		}
	}
}
