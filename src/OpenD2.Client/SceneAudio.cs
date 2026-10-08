using Godot;
using OpenD2.Assets;
using OpenD2.Core;
using System.Buffers.Binary;

namespace OpenD2.Client;

// Presentation only: bounded voices, no I/O in the tick/frame path, no simulation commands.
public partial class SceneAudio : Node
{
	private readonly AudioStreamPlayer[] voices = Enumerable.Range(0, 8).Select(_ => new AudioStreamPlayer()).ToArray();
	private readonly AudioStreamPlayer music = new();
	private Dictionary<AudioCue, AudioStreamWav> effects = new();
	private Dictionary<RegionId, AudioStreamWav> tracks = new();
	private LegacyAudioBank? bank;
	private bool synthetic, configured, suspended = true, muted;
	private float effectGain = 0.64f, musicGain = 0.4f;
	private int nextVoice;
	private RegionId region;
	public override void _Ready() { foreach (var voice in voices) AddChild(voice); AddChild(music); }
	private static AudioStreamWav Stream(PcmWave wave, bool loop) => new()
	{
		Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = wave.SampleRate, Stereo = wave.Channels == 2,
		Data = wave.Samples.ToArray(), LoopMode = loop ? AudioStreamWav.LoopModeEnum.Forward : AudioStreamWav.LoopModeEnum.Disabled,
		LoopBegin = 0, LoopEnd = wave.Frames
	};
	private static AudioStreamWav Tone(int cue)
	{
		const int rate = 22050, frames = 2205; var data = new byte[frames * 2];
		for (int i = 0; i < frames; i++)
		{
			double envelope = Math.Sin(Math.PI * i / (frames - 1));
			short sample = (short)(4000 * envelope * Math.Sin(2 * Math.PI * (220 + cue * 110) * i / rate));
			BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 2), sample);
		}
		return new() { Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = rate, Data = data };
	}
	public void SetBank(LegacyAudioBank? next, bool useSynthetic)
	{
		Stop(); region = default;
		if (configured && ReferenceEquals(bank, next) && synthetic == useSynthetic) return;
		var newEffects = new Dictionary<AudioCue, AudioStreamWav>(); var newTracks = new Dictionary<RegionId, AudioStreamWav>();
		var streams = new Dictionary<(PcmWave, bool), AudioStreamWav>();
		AudioStreamWav Cached(PcmWave wave, bool loop)
		{
			if (!streams.TryGetValue((wave, loop), out var stream)) streams.Add((wave, loop), stream = Stream(wave, loop)); return stream;
		}
		try
		{
			if (useSynthetic) foreach (var cue in Enum.GetValues<AudioCue>()) newEffects.Add(cue, Tone((int)cue));
			else if (next is not null)
			{
				foreach (var (cue, wave) in next.Effects) newEffects.Add(cue, Cached(wave, false));
				foreach (var (id, wave) in next.Music) newTracks.Add(id, Cached(wave, true));
			}
		}
		catch { foreach (var stream in newEffects.Values.Concat(streams.Values).Distinct()) stream.Dispose(); throw; }
		ClearStreams(); effects = newEffects; tracks = newTracks; bank = next; synthetic = useSynthetic; configured = true;
	}
	public void SetVolume(int master, int effect, int background, bool mute)
	{
		if (master is < 0 or > 100 || effect is < 0 or > 100 || background is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(master));
		muted = mute; effectGain = master * effect / 10000f; musicGain = master * background / 10000f;
		foreach (var voice in voices) voice.VolumeLinear = muted ? 0 : effectGain;
		music.VolumeLinear = muted ? 0 : musicGain;
		if (muted || effectGain == 0) foreach (var voice in voices) voice.Stop();
	}
	public void Sync(RegionId activeRegion, bool suspend)
	{
		if (region != activeRegion)
		{
			Stop(); region = activeRegion;
			music.Stream = tracks.GetValueOrDefault(region);
			if (music.Stream is not null) { music.Play(); music.StreamPaused = suspend; }
		}
		if (suspend != suspended)
		{
			if (suspend) foreach (var voice in voices) voice.Stop();
			music.StreamPaused = suspend; suspended = suspend;
		}
	}
	public void PlayEvents(ReadOnlySpan<SimulationEvent> events, RegionId activeRegion)
	{
		if (suspended || muted || effectGain == 0) return;
		int seen = 0;
		foreach (var e in events)
		{
			if (e.Region != activeRegion && !(e.Kind == SimulationEventKind.RegionChanged && e.Destination == activeRegion)) continue;
			if (LegacyAudioBank.CueFor(e) is not { } cue || (seen & (1 << (int)cue)) != 0) continue;
			seen |= 1 << (int)cue;
			if (!effects.TryGetValue(cue, out var stream)) continue;
			var voice = voices[nextVoice++ % voices.Length]; nextVoice %= voices.Length;
			voice.Stop(); voice.Stream = stream; voice.VolumeLinear = effectGain; voice.Play();
		}
	}
	public void Stop() { foreach (var voice in voices) voice.Stop(); music.Stop(); }
	private void ClearStreams()
	{
		foreach (var voice in voices) voice.Stream = null; music.Stream = null;
		foreach (var stream in effects.Values.Concat(tracks.Values).Distinct()) stream.Dispose(); effects.Clear(); tracks.Clear();
	}
	public override void _ExitTree() { Stop(); ClearStreams(); }
	internal void CheckSmoke()
	{
		if (tracks.Count != 1 || effects.Count != 1 || music.Stream is not AudioStreamWav { LoopMode: AudioStreamWav.LoopModeEnum.Forward } track || track.LoopEnd != track.Data.Length / 2)
			throw new InvalidDataException("Audio stream/loop mapping failed.");
		var hit = new SimulationEvent(1, SimulationEventKind.Hit, new(1), region, default, default);
		PlayEvents([hit with { Region = new(99) }], region);
		if (voices.Any(v => v.Stream is not null)) throw new InvalidDataException("Off-region audio was played.");
		PlayEvents([hit, hit, hit], region);
		if (voices.Count(v => v.Stream is not null) != 1 || GetChildCount() != 9) throw new InvalidDataException("Audio event coalescing/voice bound failed.");
		Sync(region, true); if (!music.StreamPaused) throw new InvalidDataException("Music pause failed.");
		PlayEvents([hit], region); if (voices.Any(v => v.Playing)) throw new InvalidDataException("Suspended effects were played.");
		Sync(region, false); if (music.StreamPaused) throw new InvalidDataException("Music resume failed.");
		SetVolume(100, 50, 25, true); if (music.VolumeLinear != 0) throw new InvalidDataException("Music mute failed.");
		SetVolume(100, 50, 25, false); if (Math.Abs(music.VolumeLinear - 0.25f) > 0.001) throw new InvalidDataException("Music volume failed.");
		Stop(); if (music.Playing) throw new InvalidDataException("Audio stop failed.");
	}
}
