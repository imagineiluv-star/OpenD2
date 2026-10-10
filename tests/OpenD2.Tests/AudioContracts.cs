using System.Buffers.Binary;
using OpenD2.Assets;
using OpenD2.Core;

internal static class AudioContracts
{
	private static void Check(bool value) { if (!value) throw new Exception("Audio assertion failed."); }
	private static void Bad(Action action) { try { action(); } catch (Exception e) when (e is InvalidDataException or ArgumentException or FileNotFoundException) { return; } throw new Exception("Expected invalid audio."); }
	private static byte[] Wave(byte[] samples, ushort bits = 16, ushort channels = 1)
	{
		var data = new byte[44 + samples.Length + (samples.Length & 1)];
		"RIFF"u8.CopyTo(data); BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)data.Length - 8);
		"WAVEfmt "u8.CopyTo(data.AsSpan(8)); BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 16);
		BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(20), 1); BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(22), channels);
		BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), 8000); BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(28), (uint)(8000 * channels * bits / 8));
		BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(32), (ushort)(channels * bits / 8)); BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(34), bits);
		"data"u8.CopyTo(data.AsSpan(36)); BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(40), (uint)samples.Length); samples.CopyTo(data, 44); return data;
	}
	public static void Run(Action<string, Action> test)
	{
		test("PCM8 conversion preserves zero and full scale with padded chunks", () =>
		{
			var bytes = Wave([0, 128, 255], 8); var copy = bytes.ToArray(); var pcm = PcmWave.Parse(bytes);
			Check(pcm.SampleRate == 8000 && pcm.Channels == 1 && pcm.Frames == 3 && pcm.Samples.Span.SequenceEqual(new byte[] { 0, 128, 0, 0, 0, 127 }) && bytes.SequenceEqual(copy));
			bytes[44] = 128; Check(pcm.Samples.Span[1] == 128);
		});
		test("PCM16 stereo preserves frames and unsigned RIFF chunk bounds", () =>
		{
			byte[] samples = [0, 128, 255, 127, 1, 0, 255, 255]; var pcm = PcmWave.Parse(Wave(samples, 16, 2));
			Check(pcm.Frames == 2 && pcm.Channels == 2 && pcm.Samples.Span.SequenceEqual(samples));
			var broken = Wave(samples); BinaryPrimitives.WriteUInt32LittleEndian(broken.AsSpan(40), uint.MaxValue); Bad(() => PcmWave.Parse(broken));
		});
		test("WAV rejects codecs, broken lengths, alignment and duplicate chunks", () =>
		{
			foreach (int offset in new[] { 0, 4, 8, 20, 22, 24, 28, 32, 34, 40 })
			{ var bytes = Wave([0, 0, 1, 0]); bytes[offset] ^= 0x7f; Bad(() => PcmWave.Parse(bytes)); }
			Bad(() => PcmWave.Parse(Wave([1], 16))); Bad(() => PcmWave.Parse(Wave([])));
			var odd = Wave([128], 8); Bad(() => PcmWave.Parse(odd[..^1]));
			var valid = Wave([0, 0]); var duplicate = valid.Concat(valid[36..]).ToArray();
			BinaryPrimitives.WriteUInt32LittleEndian(duplicate.AsSpan(4), (uint)duplicate.Length - 8); Bad(() => PcmWave.Parse(duplicate));
		});
		test("WAV skips bounded unknown metadata and enforces decoded budget", () =>
		{
			var valid = Wave([0, 0]); var bytes = valid.Concat("JUNK"u8.ToArray()).Concat(new byte[] { 2, 0, 0, 0, 1, 2 }).ToArray();
			BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)bytes.Length - 8); Check(PcmWave.Parse(bytes).Frames == 1);
			Bad(() => PcmWave.Parse(Wave(new byte[PcmWave.MaxDecodedBytes / 2 + 1], 8)));
		});
		test("WAV audit accepts large PCM without allocating decoded samples; runtime stays bounded", () =>
		{
			var wave = Wave(new byte[40 * 1024 * 1024]);
			PcmWave.Validate(wave); // warm up before measuring this thread
			long before = GC.GetAllocatedBytesForCurrentThread();
			PcmWave.Validate(wave);
			Check(GC.GetAllocatedBytesForCurrentThread() - before < 4096);
			Bad(() => PcmWave.Parse(wave));
			wave[40] ^= 1; Bad(() => PcmWave.Validate(wave));
			Bad(() => PcmWave.Validate(new byte[PcmWave.MaxAuditInputBytes + 1]));
		});
		test("WAV audit preserves malformed header, codec and chunk rejection", () =>
		{
			foreach (int offset in new[] { 0, 4, 8, 20, 22, 24, 28, 32, 34, 40 })
			{ var wave = Wave([0, 0]); wave[offset] ^= 0x7f; Bad(() => PcmWave.Validate(wave)); }
			Bad(() => PcmWave.Validate(Wave([128], 8)[..^1]));
		});
		test("demo opaque records require exact path, archive, length and hash", () =>
		{
			const string path = @"data\global\sfx\cursor\curindx.wav";
			Check(DemoOpaqueRecords.IsRecord("d2sfx.mpq", path));
			Check(!DemoOpaqueRecords.IsRecord("patch_d2.mpq", path));
			Check(!DemoOpaqueRecords.IsRecord("d2sfx.mpq", "other.wav"));
			Bad(() => DemoOpaqueRecords.Validate(path, new byte[72]));
			Bad(() => DemoOpaqueRecords.Validate(path, new byte[71]));
			Bad(() => DemoOpaqueRecords.Validate("other.wav", new byte[72]));
			Check(AssetDecoders.Kind(path) == "wav_pcm"); // no global extension/path bypass
		});
		test("audio bank normalizes and shares MPQ reads across cues and regions", () =>
		{
			int reads = 0; var request = new LegacyAudioRequest([new("Hit", "DATA/SFX/A.WAV"), new("Death", "data/sfx/a.wav")], [new(1, "data/sfx/a.wav"), new(2, "data/sfx/a.wav")]);
			var bank = LegacyAudioBank.Load(request, [new(1), new(2)], path => { Check(path == "data\\sfx\\a.wav"); reads++; return Wave([0, 0]); });
			Check(reads == 1 && ReferenceEquals(bank.Effects[AudioCue.Hit], bank.Music[new(2)]));
			request.Effects[0] = new("Hit", "other.wav"); Check(bank.Effects.Count == 2);
		});
		test("invalid audio mappings fail before reading files", () =>
		{
			foreach (var request in new[] {
				new LegacyAudioRequest([new("0", "a.wav")], []), new([new("hit", "a.wav")], []), new([new("Hit", "../a.wav")], []),
				new([new("Hit", "a.mp3")], []), new([new("Hit", "a.wav"), new("Hit", "b.wav")], []), new([], [new(9, "a.wav")]),
				new([], [new(1, "a.wav"), new(1, "b.wav")]), new(null!, []), new([null!], []) })
				Bad(() => LegacyAudioBank.Load(request, [new(1)], _ => throw new Exception("Unexpected read")));
		});
		test("audio bank rejects missing resources, long effects and combined PCM budget", () =>
		{
			Bad(() => LegacyAudioBank.Load(new([new("Hit", "missing.wav")], []), [new(1)], _ => throw new FileNotFoundException()));
			Bad(() => LegacyAudioBank.Load(new([new("Hit", "long.wav")], []), [new(1)], _ => Wave(new byte[160002])));
			Bad(() => LegacyAudioBank.Load(new([], [new(1, "a.wav"), new(2, "b.wav"), new(3, "c.wav")]), [new(1), new(2), new(3)], _ => Wave(new byte[12 * 1024 * 1024], 8)));
		});
		test("optional scene audio leaves content identity, save compatibility and replay unchanged", () =>
		{
			var request = PlaySceneContracts.Request(); var original = LegacyPlayScene.Load(request, PlayAssetContracts.Read);
			var audio = LegacyPlayScene.Load(request with { Audio = new([new("Hit", "a.wav")], [new(2, "a.wav")]) }, p => p.EndsWith(".wav") ? Wave([0, 0]) : PlayAssetContracts.Read(p));
			Check(audio.Audio is not null && original.Audio is null && audio.ContentId == original.ContentId);
			var a = original.Create(1); var b = audio.Create(1);
			for (int i = 0; i < 60; i++) { a.Step(); b.Step(); foreach (var e in b.Events) _ = LegacyAudioBank.CueFor(e); }
			Check(a.ComputeStateHash() == b.ComputeStateHash());
		});
		test("audio event mapping ignores movement and failed commands", () =>
		{
			var e = new SimulationEvent(1, SimulationEventKind.ItemChanged, new(1), new(1), default, default, (int)CommandKind.Pickup);
			Check(LegacyAudioBank.CueFor(e) == AudioCue.Loot && LegacyAudioBank.CueFor(e with { Value = (int)CommandKind.Equip }) is null);
			foreach (var kind in new[] { SimulationEventKind.Moved, SimulationEventKind.AttackFailed, SimulationEventKind.ItemFailed, SimulationEventKind.NpcTalked }) Check(LegacyAudioBank.CueFor(e with { Kind = kind }) is null);
			Check(AssetDecoders.Kind("data/music/track.wav") == "wav_pcm"); AssetDecoders.Validate("wav_pcm", Wave([0, 0]));
		});
		test("audio settings validate all channel bounds", () =>
		{
			foreach (var setting in new[] { new AppSettings(MasterVolume: -1), new AppSettings(EffectsVolume: 101), new AppSettings(MusicVolume: 101) }) Bad(setting.Validate);
			new AppSettings(MasterVolume: 0, EffectsVolume: 100, MusicVolume: 0, Muted: true).Validate();
		});
	}
}
