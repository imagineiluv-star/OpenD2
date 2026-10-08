using System.Collections.ObjectModel;
using OpenD2.Core;

namespace OpenD2.Assets;

public enum AudioCue { Attack, Hit, Death, Loot, Quest, Portal }
public sealed record LegacySoundRequest(string Cue, string Path);
public sealed record LegacyMusicRequest(uint Region, string Path);
public sealed record LegacyAudioRequest(LegacySoundRequest[] Effects, LegacyMusicRequest[] Music);

// Optional presentation data. No network calls, simulation state changes or save identity changes.
public sealed class LegacyAudioBank
{
	public IReadOnlyDictionary<AudioCue, PcmWave> Effects { get; }
	public IReadOnlyDictionary<RegionId, PcmWave> Music { get; }
	private LegacyAudioBank(Dictionary<AudioCue, PcmWave> effects, Dictionary<RegionId, PcmWave> music)
	{ Effects = new ReadOnlyDictionary<AudioCue, PcmWave>(effects); Music = new ReadOnlyDictionary<RegionId, PcmWave>(music); }
	public static LegacyAudioBank Load(LegacyAudioRequest request, IEnumerable<RegionId> regions, Func<string, byte[]> read)
	{
		ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(regions); ArgumentNullException.ThrowIfNull(read);
		AssetBinary.Require(request.Effects is { Length: <= 6 } && request.Music is { Length: <= WorldDefinition.MaxRegions }, "Audio cue/region count exceeds budget.");
		var known = regions.ToHashSet(); var cues = new Dictionary<AudioCue, string>(); var tracks = new Dictionary<RegionId, string>();
		string Path(string path)
		{
			string normalized = MpqArchive.NormalizePath(path);
			AssetBinary.Require(normalized.EndsWith(".wav", StringComparison.Ordinal), "Audio paths must reference WAV files in MPQs."); return normalized;
		}
		foreach (var sound in request.Effects!)
		{
			AssetBinary.Require(sound is not null && Enum.TryParse<AudioCue>(sound.Cue, out var parsed) && Enum.IsDefined(parsed) && parsed.ToString() == sound.Cue, "Unknown audio cue.");
			AssetBinary.Require(cues.TryAdd(Enum.Parse<AudioCue>(sound!.Cue), Path(sound.Path)), "Duplicate audio cue.");
		}
		foreach (var track in request.Music!)
		{
			AssetBinary.Require(track is not null && known.Contains(new(track.Region)), "Music references an unknown region.");
			AssetBinary.Require(tracks.TryAdd(new(track!.Region), Path(track.Path)), "Duplicate region music.");
		}
		var cache = new Dictionary<string, PcmWave>(StringComparer.Ordinal); long input = 0, decoded = 0;
		PcmWave Load(string path)
		{
			if (cache.TryGetValue(path, out var found)) return found;
			var bytes = read(path); input += bytes.LongLength;
			AssetBinary.Require(input <= 64L * 1024 * 1024, "Audio input exceeds 64 MiB.");
			var wave = PcmWave.Parse(bytes); decoded += wave.Samples.Length;
			AssetBinary.Require(decoded <= 64L * 1024 * 1024, "Decoded audio exceeds 64 MiB."); cache.Add(path, wave); return wave;
		}
		var effects = new Dictionary<AudioCue, PcmWave>();
		foreach (var (cue, path) in cues)
		{
			var wave = Load(path); AssetBinary.Require(wave.Frames <= wave.SampleRate * 10, "Effects must not exceed 10 seconds."); effects.Add(cue, wave);
		}
		return new(effects, tracks.ToDictionary(p => p.Key, p => Load(p.Value)));
	}
	public static AudioCue? CueFor(SimulationEvent e) => e.Kind switch
	{
		SimulationEventKind.AttackStarted => AudioCue.Attack, SimulationEventKind.Hit => AudioCue.Hit,
		SimulationEventKind.Died => AudioCue.Death, SimulationEventKind.ItemDropped => AudioCue.Loot,
		SimulationEventKind.ItemChanged when (CommandKind)e.Value == CommandKind.Pickup => AudioCue.Loot,
		SimulationEventKind.QuestChanged => AudioCue.Quest, SimulationEventKind.RegionChanged => AudioCue.Portal,
		_ => null
	};
}
