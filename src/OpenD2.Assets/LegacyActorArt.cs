using System.Collections.ObjectModel;
using OpenD2.Core;

namespace OpenD2.Assets;

public enum ActorMotion { Idle, Walk, Attack, Hit, Death }
public sealed record LegacyMotionRequest(string Motion, string Path, Dictionary<byte, string>? Layers, int[] Directions, double Fps);
public sealed record LegacyActorRequest(uint Entity, string PalettePath, LegacyMotionRequest[] Motions);
public sealed record LegacyMotion(ActorMotion Motion, IReadOnlyList<AnimationClip> Directions, double Fps);

// Explicit paths and direction indices: no guessed class/equipment filename conventions.
public sealed class LegacyActorArt
{
	public Palette Palette { get; }
	public IReadOnlyDictionary<ActorMotion, LegacyMotion> Motions { get; }
	public long PixelCount { get; }
	private LegacyActorArt(Palette palette, Dictionary<ActorMotion, LegacyMotion> motions, long pixels)
	{ Palette = palette; Motions = new ReadOnlyDictionary<ActorMotion, LegacyMotion>(motions); PixelCount = pixels; }
	public static LegacyActorArt Load(LegacyActorRequest request, Func<string, byte[]> read)
	{
		if (request is null || request.Entity == 0 || request.Motions is null || request.Motions.Length != 5 || request.Motions.Any(m => m is null) || AssetDecoders.Kind(request.PalettePath) != "palette")
			throw new InvalidDataException("Actor artwork requires an entity, palette and five motion definitions.");
		var definitions = request.Motions.Select(m => m with { Directions = m.Directions?.ToArray()!, Layers = m.Layers is null ? null : new(m.Layers) }).ToArray();
		var motionTypes = new HashSet<ActorMotion>();
		foreach (var def in definitions)
		{
			if (!Enum.TryParse<ActorMotion>(def.Motion, false, out var type) || !Enum.IsDefined(type) || def.Motion != type.ToString() || !motionTypes.Add(type) ||
				def.Directions is null || def.Directions.Length != 8 || def.Directions.Any(d => d is < 0 or > 31) || !double.IsFinite(def.Fps) || def.Fps is <= 0 or > 120)
				throw new InvalidDataException("Artwork needs unique Idle/Walk/Attack/Hit/Death, eight explicit directions and valid FPS.");
			string? kind = AssetDecoders.Kind(def.Path);
			if (kind is not ("dcc" or "cof") || (kind == "dcc" && def.Layers is not null) ||
				(kind == "cof" && (def.Layers is null || def.Layers.Count is < 1 or > 16 || def.Layers.Any(p => p.Key > 15 || AssetDecoders.Kind(p.Value) != "dcc"))))
				throw new InvalidDataException("Specify a DCC or a COF with explicit component DCC paths.");
		}
		long input = 0, pixels = 0;
		byte[] Read(string path)
		{
			var bytes = read(MpqArchive.NormalizePath(path)); input += bytes.LongLength;
			if (bytes.Length > AssetDecoders.MaxInputBytes || input > 64L * 1024 * 1024) throw new InvalidDataException("Actor artwork input budget exceeded."); return bytes;
		}
		var palette = Palette.Parse(Read(request.PalettePath)); var motions = new Dictionary<ActorMotion, LegacyMotion>();
		foreach (var def in definitions)
		{
			var data = Read(def.Path); bool isCof = AssetDecoders.Kind(def.Path) == "cof";
			var cof = isCof ? CofAnimation.Parse(data) : null; var single = isCof ? null : DccAnimation.Parse(data);
			var layers = def.Layers?.ToDictionary(p => p.Key, p => DccAnimation.Parse(Read(p.Value)));
			var decoded = new Dictionary<int, AnimationClip>();
			foreach (int direction in def.Directions.Distinct())
			{
				var clip = cof is null ? AnimationClip.Single(single!.DecodeDirection(direction)) :
					AnimationClip.Compose(cof, direction, layers!.ToDictionary(p => p.Key, p => p.Value.DecodeDirection(direction)));
				pixels += (long)clip.Width * clip.Height * clip.Frames.Count;
				if (pixels > DccAnimation.MaxPixels) throw new InvalidDataException("Actor artwork decoded pixel budget exceeded.");
				decoded.Add(direction, clip);
			}
			var type = Enum.Parse<ActorMotion>(def.Motion);
			motions.Add(type, new(type, Array.AsReadOnly(def.Directions.Select(d => decoded[d]).ToArray()), def.Fps));
		}
		return new(palette, motions, pixels);
	}
}

// Presentation timing only. It never changes combat state or consumes the simulation RNG.
public sealed class ActorAnimationClock
{
	public ActorMotion Motion { get; private set; }
	public int Facing { get; private set; }
	private long entered, lastTick = -1;
	public void Face(int x, int y)
	{
		Facing = (Math.Sign(x), Math.Sign(y)) switch
		{ (-1, -1) => 0, (0, -1) => 1, (1, -1) => 2, (1, 0) => 3, (1, 1) => 4, (0, 1) => 5, (-1, 1) => 6, (-1, 0) => 7, _ => Facing };
	}

	public int Frame(EntityState state, long tick, LegacyActorArt art)
	{
		if (tick < 0) throw new ArgumentOutOfRangeException(nameof(tick));
		ActorMotion next = !state.IsAlive ? ActorMotion.Death : state.HitStun > 0 ? ActorMotion.Hit : state.AttackCooldown > 0 ? ActorMotion.Attack :
			state.MoveX != 0 || state.MoveY != 0 ? ActorMotion.Walk : ActorMotion.Idle;
		if (lastTick < 0 || tick < lastTick || next != Motion) { entered = tick; Motion = next; }
		if (next == ActorMotion.Walk) Face(state.MoveX, state.MoveY);
		lastTick = tick; var motion = art.Motions[Motion]; int count = motion.Directions[Facing].Frames.Count;
		double frame = (tick - entered) * motion.Fps / 25;
		return Motion is ActorMotion.Death or ActorMotion.Attack or ActorMotion.Hit ? (int)Math.Min(count - 1, frame) : (int)(frame % count);
	}
}
