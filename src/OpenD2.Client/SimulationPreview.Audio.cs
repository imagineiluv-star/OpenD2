using Godot;
using OpenD2.Assets;
using OpenD2.Core;

namespace OpenD2.Client;

public partial class SimulationPreview
{
	private void AudioSmoke()
	{
		// Small synthetic WAV, never original game data. This tests control/stream state, not speaker output.
		byte[] wave = Convert.FromHexString("524946462800000057415645666D74201000000001000100401F0000803E000002001000646174610400000000001000");
		var bank = LegacyAudioBank.Load(new([new("Hit", "sample.wav")], [new(1, "sample.wav")]), [Region], _ => wave);
		string before = simulation.ComputeStateHash();
		audio.SetBank(bank, false); audio.Sync(Region, false); audio.CheckSmoke();
		if (simulation.ComputeStateHash() != before) throw new InvalidDataException("Audio changed game state.");
		audio.SetBank(null, true); audio.SetVolume(80, 80, 50, false); SyncAudio();
		GD.Print("OPEND2_M206_AUDIO_READY");
	}
}
