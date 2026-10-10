using System.Buffers.Binary;
using System.Security.Cryptography;
using OpenD2.Assets;

internal static class MusicProbe
{
	// Virtual MPQ name is used only inside this exact, SHA-pinned public-demo archive.
	public static object CheckDemo(string directory, string scenePath)
	{
		string path = Path.Combine(DataDirectory.Validate(directory), "d2music.mpq");
		using (var file = File.OpenRead(path))
			if (Convert.ToHexStringLower(SHA256.HashData(file)) != "631172d59cc4a8d9b42faade73b194140b6a327811ea556562df9c89f857a694")
				throw new InvalidDataException("Expected pinned public-demo music archive.");
		using var archive = new MpqArchive(path);
		var bytes = archive.Read("file00000004.wav", PcmWave.MaxAuditInputBytes);
		string sourceHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
		if (bytes.Length != 42270644 || sourceHash != "5792977e500540ae15139f66630b9a3967d445fb3436e59b7be323d840af824c")
			throw new InvalidDataException("Unexpected demo music entry.");
		// Use the same bank + cursor path as the client; do not promote this to speaker/GUI acceptance.
		var bank = LegacyAudioBank.Load(new([], [new(1, "file00000004.wav")]), [new(1)], _ => bytes);
		var wave = bank.Music[new(1)]; var cursor = new PcmLoop(wave);
		var frames = new float[2048]; var pcm = new byte[1024 * wave.Channels * 2];
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		int remaining = wave.Frames;
		while (remaining > 0)
		{
			int count = Math.Min(1024, remaining); cursor.ReadStereo(frames.AsSpan(0, count * 2));
			for (int i = 0; i < count; i++) for (int channel = 0; channel < wave.Channels; channel++)
				BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan((i * wave.Channels + channel) * 2), checked((short)(frames[i * 2 + channel] * 32768)));
			hash.AppendData(pcm, 0, count * wave.Channels * 2); remaining -= count;
		}
		string pcmHash = Convert.ToHexStringLower(hash.GetHashAndReset());
		if (pcmHash != Convert.ToHexStringLower(SHA256.HashData(wave.Samples.Span)) || cursor.Position != 0)
			throw new InvalidDataException("Music cursor dropped/reordered/changed PCM samples.");
		var first = new float[256]; var repeated = new float[256]; cursor.ReadStereo(first); cursor.Reset(); cursor.ReadStereo(repeated);
		if (!first.SequenceEqual(repeated)) throw new InvalidDataException("Music loop/reset mismatch.");
		var request = LegacySceneRequest.Read(scenePath) with
		{ Audio = new([], [new(1, "data/global/music/act1/town1.wav")]) };
		var scene = LegacyPlayScene.Load(directory, request);
		if (scene.ContentId != "ce384cadb59d616824b0fc2c8c92199a785f666a3d65bb7b567ed309b16636de" ||
			scene.Audio is null || scene.Audio.Music.Count != 1 ||
			scene.Audio.Music[new(1)].SampleRate != 22050 || scene.Audio.Music[new(1)].Channels != 2)
			throw new InvalidDataException("Demo scene music binding or content identity changed.");
		return new { Status = "PASS", Scope = "actual_demo_pcm_cursor", SourceBytes = bytes.Length, SourceHash = sourceHash,
			wave.SampleRate, wave.Channels, wave.Frames, PcmHash = pcmHash, EntireTrackChecked = true,
			LoopChecked = true, SceneMusicLoaded = true, SceneContentId = scene.ContentId, Gui = "NOT_RUN", SpeakerOutput = "NOT_RUN" };
	}
}
