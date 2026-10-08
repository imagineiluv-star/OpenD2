using Godot;
using OpenD2.Assets;
using OpenD2.Core;

namespace OpenD2.Client;

public partial class SimulationPreview
{
	private IReadOnlyList<GamePosition> route = [];
	private int routeIndex, blockedRouteTicks;
	private EntityId chaseTarget;
	private void ClearRoute() { route = []; routeIndex = 0; blockedRouteTicks = 0; chaseTarget = default; }
	private void NavigationSmoke()
	{
		ClickMove(new(640, 640));
		for (int i = 0; i < 32; i++)
		{
			var next = FollowRoute(true, 0, 0);
			if (next.X != requestedX || next.Y != requestedY)
			{ if (!Submit(CommandKind.SetMove, next.X, next.Y)) throw new InvalidDataException("Route smoke command rejected."); requestedX = next.X; requestedY = next.Y; }
			RunTick();
		}
		if (Math.Abs(current.Position.X - 640) > 16 || Math.Abs(current.Position.Y - 640) > 16 || route.Count != 0)
			throw new InvalidDataException("Client route following smoke failed.");
		GD.Print("OPEND2_PLAY04_NAVIGATION_READY");
	}
	private void ClickMove(GamePosition destination)
	{
		if (verifying || paused || !current.IsAlive || simulation.Collision is not { } grid) return;
		ClearRoute(); var path = GridPathfinder.Find(grid, current.Position, destination);
		if (path.Status != PathStatus.Found) { status.Text = "Click move: " + path.Status; return; }
		// Align within the current cell before following center-to-center segments.
		var center = new GamePosition(grid.Origin.X + (current.Position.X - grid.Origin.X) / 256 * 256 + 128,
			grid.Origin.Y + (current.Position.Y - grid.Origin.Y) / 256 * 256 + 128);
		route = new[] { center }.Concat(path.Points).ToArray(); status.Text = $"Click move: {route.Count} waypoints.";
	}
	private (int X, int Y) FollowRoute(bool active, int keyX, int keyY)
	{
		if (!active || paused || keyX != 0 || keyY != 0) { ClearRoute(); return (keyX, keyY); }
		if (chaseTarget != default)
		{
			var target = simulation.GetEntity(chaseTarget);
			if (!target.IsAlive || target.Region != current.Region) { ClearRoute(); return (0, 0); }
			if (simulation.Collision!.HasMeleeLine(current.Position, target.Position))
			{ var id = chaseTarget; StopInput(); if (Submit(CommandKind.Attack, target: id)) lastAttackTick = simulation.Tick; return (0, 0); }
		}
		while (routeIndex < route.Count)
		{
			var next = route[routeIndex]; int dx = next.X - current.Position.X, dy = next.Y - current.Position.Y;
			if (Math.Abs(dx) <= 16 && Math.Abs(dy) <= 16) { routeIndex++; continue; }
			return (Math.Abs(dx) <= 16 ? 0 : Math.Sign(dx), Math.Abs(dy) <= 16 ? 0 : Math.Sign(dy));
		}
		ClearRoute(); return (0, 0);
	}
	private void ObserveRouteTick()
	{
		if (route.Count == 0) return;
		if (current.Position == previous.Position) blockedRouteTicks++; else blockedRouteTicks = 0;
		if (blockedRouteTicks >= 25) { ClearRoute(); status.Text = "Route blocked by an actor or obstacle; click a new destination."; }
	}
	private void ClickAttack(EntityId target)
	{
		if (verifying || paused || !current.IsAlive) return;
		ClearRoute(); StopInput();
		var entity = simulation.GetEntity(target);
		if (simulation.Collision!.HasMeleeLine(current.Position, entity.Position))
		{ if (Submit(CommandKind.Attack, target: target)) lastAttackTick = simulation.Tick; }
		else { ClickMove(entity.Position); if (route.Count > 0) chaseTarget = target; }
	}
}

public partial class SimulationCanvas
{
	public event Action<GamePosition>? MoveRequested;
	public event Action<EntityId>? AttackRequested;
	private void ClickWorld(Vector2 at)
	{
		if (simulation?.Collision is null) return;
		try
		{
			var relative = at - Size / 2;
			GamePosition position = terrainContent is null ? new((int)Math.Round(DisplayPosition.X + relative.X * 256 / 40), (int)Math.Round(DisplayPosition.Y + relative.Y * 256 / 40)) :
				LegacyProjection.Unproject(Iso(DisplayPosition.X, DisplayPosition.Y).X + relative.X, Iso(DisplayPosition.X, DisplayPosition.Y).Y + relative.Y);
			EntityId target = default; float nearest = 18 * 18;
			foreach (var entity in simulation.Entities)
			{
				if (entity.Kind != EntityKind.Monster || !entity.IsAlive || entity.Region != simulation.ActiveRegion) continue;
				var point = terrainContent is null ? Size / 2 + (new Vector2(entity.Position.X, entity.Position.Y) - DisplayPosition) / 256 * 40 :
					Size / 2 + Iso(entity.Position.X - DisplayPosition.X, entity.Position.Y - DisplayPosition.Y);
				float distance = point.DistanceSquaredTo(at); if (distance < nearest) { nearest = distance; target = entity.Id; }
			}
			if (target != default) AttackRequested?.Invoke(target); else MoveRequested?.Invoke(position);
		}
		catch (ArgumentOutOfRangeException) { } // A click outside bounded world coordinates is ignored.
	}
}
