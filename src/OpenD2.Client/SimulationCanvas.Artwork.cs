using Godot;
using OpenD2.Assets;
using OpenD2.Core;

namespace OpenD2.Client;

public partial class SimulationCanvas
{
	private sealed class ActorSprite : IDisposable
	{
		public LegacyActorArt Art { get; }
		public ActorAnimationClock Clock { get; private set; } = new();
		public ImageTexture Texture { get; private set; }
		public AnimationClip Clip { get; private set; }
		private int frame;
		public ActorSprite(LegacyActorArt art)
		{
			Art = art; Clip = art.Motions[ActorMotion.Idle].Directions[0];
			using var image = Image.CreateFromData(Clip.Width, Clip.Height, false, Image.Format.Rgba8, Clip.ToRgba(0, art.Palette));
			Texture = ImageTexture.CreateFromImage(image);
		}
		public void Reset() { Clock = new(); frame = -1; }
		public void Update(EntityState state, long tick)
		{
			int nextFrame = Clock.Frame(state, tick, Art); var nextClip = Art.Motions[Clock.Motion].Directions[Clock.Facing];
			if (nextFrame == frame && ReferenceEquals(nextClip, Clip)) return;
			using var image = Image.CreateFromData(nextClip.Width, nextClip.Height, false, Image.Format.Rgba8, nextClip.ToRgba(nextFrame, Art.Palette));
			if (nextClip.Width == Clip.Width && nextClip.Height == Clip.Height) Texture.Update(image);
			else { var next = ImageTexture.CreateFromImage(image); Texture.Dispose(); Texture = next; }
			Clip = nextClip; frame = nextFrame;
		}
		public void Dispose() { Texture.Dispose(); }
	}
	private Dictionary<EntityId, ActorSprite> actorSprites = new();
	private static Dictionary<EntityId, ActorSprite> PrepareArtwork(LegacyPlayScene? content)
	{
		var prepared = new Dictionary<EntityId, ActorSprite>();
		try
		{
			if (content is not null) foreach (var pair in content.Artwork) prepared.Add(pair.Key, new(pair.Value));
			return prepared;
		}
		catch { foreach (var sprite in prepared.Values) sprite.Dispose(); throw; }
	}
	private bool DrawActor(EntityState actor, Vector2 point)
	{
		if (!actorSprites.TryGetValue(actor.Id, out var sprite) || simulation is null) return false;
		foreach (var change in simulation.Events)
			if (change.Actor == actor.Id && change.Kind == SimulationEventKind.AttackStarted)
				sprite.Clock.Face(change.To.X - change.From.X, change.To.Y - change.From.Y);
		sprite.Update(actor, simulation.Tick);
		DrawTexture(sprite.Texture, point + new Vector2(sprite.Clip.Left, sprite.Clip.Top)); return true;
	}
	private void ClearArtwork()
	{
		foreach (var sprite in actorSprites.Values) sprite.Dispose(); actorSprites.Clear();
	}
	public bool CheckActorTexture()
	{
		if (simulation is null || !actorSprites.TryGetValue(new(1), out var sprite)) return false;
		sprite.Update(simulation.GetEntity(new(1)), simulation.Tick);
		using var image = sprite.Texture.GetImage();
		return image.GetWidth() == sprite.Clip.Width && image.GetHeight() == sprite.Clip.Height && image.GetData().Where((_, index) => index % 4 == 3).Any(alpha => alpha != 0);
	}
}
