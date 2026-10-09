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
			int nextFrame = Clock.Frame(state, tick, Art);
			UpdateFrame(Art.Motions[Clock.Motion].Directions[Clock.Facing], nextFrame);
		}
		public void UpdateIdle(long tick, int facing) => UpdateFrame(Art.Motions[ActorMotion.Idle].Directions[facing], Art.IdleFrame(tick, facing));
		private void UpdateFrame(AnimationClip nextClip, int nextFrame)
		{
			if (nextFrame == frame && ReferenceEquals(nextClip, Clip)) return;
			using var image = Image.CreateFromData(nextClip.Width, nextClip.Height, false, Image.Format.Rgba8, nextClip.ToRgba(nextFrame, Art.Palette));
			if (nextClip.Width == Clip.Width && nextClip.Height == Clip.Height) Texture.Update(image);
			else { var next = ImageTexture.CreateFromImage(image); Texture.Dispose(); Texture = next; }
			Clip = nextClip; frame = nextFrame;
		}
		public void Dispose() { Texture.Dispose(); }
	}
	private Dictionary<EntityId, ActorSprite> actorSprites = new();
	private ActorSprite? npcSprite;
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
			if (change.Actor == actor.Id && change.Kind is SimulationEventKind.AttackStarted or SimulationEventKind.SkillCast)
				sprite.Clock.Face(change.To.X - change.From.X, change.To.Y - change.From.Y);
		sprite.Update(actor, simulation.Tick);
		DrawTexture(sprite.Texture, point + new Vector2(sprite.Clip.Left, sprite.Clip.Top)); return true;
	}
	private bool DrawNpcArtwork(Vector2 point)
	{
		if (npcSprite is null || simulation is null || terrainContent is null) return false;
		npcSprite.UpdateIdle(simulation.Tick, terrainContent.NpcFacing);
		DrawTexture(npcSprite.Texture, point + new Vector2(npcSprite.Clip.Left, npcSprite.Clip.Top)); return true;
	}
	public bool CheckNpcTexture()
	{
		if (npcSprite is null || simulation is null || terrainContent is null) return false;
		npcSprite.UpdateIdle(simulation.Tick, terrainContent.NpcFacing);
		using var image = npcSprite.Texture.GetImage();
		return image.GetWidth() == npcSprite.Clip.Width && image.GetHeight() == npcSprite.Clip.Height && image.GetData().Where((_, index) => index % 4 == 3).Any(alpha => alpha != 0);
	}
	private void ClearArtwork()
	{
		npcSprite?.Dispose(); npcSprite = null;
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
