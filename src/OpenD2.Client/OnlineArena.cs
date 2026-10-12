using Godot;
using OpenD2.Core;
using OpenD2.Online;

namespace OpenD2.Client;

public partial class OnlineArena : Control
{
    private static readonly CollisionGrid arenaGrid = Arena.Grid();
    private CollisionGrid grid = arenaGrid;
    private WorldDefinition? world;
    private readonly RoomPresentation presentation = new();
    private RoomPrediction prediction = new(arenaGrid);
    private RoomView? state;
    private bool wasAnimating;
    private static double Now => Time.GetTicksUsec() / 1_000_000.0;
    public RoomView? State { get => state; set => SetState(value, Now); }
    internal long PresentationTransitions => presentation.Transitions;
    internal CollisionGrid Grid => grid;
    internal IEnumerable<EntityState> VisibleEntities => state?.Entities.Where(e => e.Region == grid.Region) ?? [];
    internal IEnumerable<ItemState> VisibleGroundItems => state?.Items.Where(i => i.Location == ItemLocation.Ground && i.Region == grid.Region) ?? [];
    internal void SetState(RoomView? value, double now)
    {
        if (value is null)
        {
            presentation.Reset(); prediction = new(arenaGrid); grid = arenaGrid; world = null; state = null; wasAnimating = false;
        }
        else
        {
            bool same = state?.Id == value.Id && state?.Actor == value.Actor;
            if (same && (value.Tick < state!.Tick || value.World?.RegionEpoch < state.World?.RegionEpoch)) return;
            // Resolve before changing live rendering or prediction. A mismatched map
            // must never be displayed as the Arena or used for local collision.
            var definition = value.World is { } selected ? CooperativeWorld.Resolve(selected.Content) : null;
            if (same && state!.World?.Content != value.World?.Content) throw new InvalidDataException("Room content changed.");
            var nextGrid = definition is null ? arenaGrid : definition.GetRegion(value.Started
                ? value.Entities.Single(e => e.Id.Value == value.Actor).Region : definition.Regions[0].Id).Collision;
            bool changed = !same || !ReferenceEquals(grid, nextGrid) || state?.World?.RegionEpoch != value.World?.RegionEpoch;
            if (changed) { presentation.Reset(); prediction = new(nextGrid); wasAnimating = false; }
            grid = nextGrid; world = definition;
            if (presentation.Push(value, now)) { prediction.Push(value, now); state = value; }
        }
        QueueRedraw();
    }
    internal void ResetMotion()
    {
        presentation.Reset(); prediction.Reset(); wasAnimating = false;
        if (state is { } current) { presentation.Push(current, Now); prediction.Push(current, Now); }
        QueueRedraw();
    }
    internal EntityId NearbyInteraction()
    {
        if (state is not { Started: true } || world is null) return default;
        var self = state.Entities.Single(e => e.Id.Value == state.Actor);
        if (!self.IsAlive) return default;
        EntityId target = default; long nearest = long.MaxValue;
        void Consider(EntityId id, RegionId region, GamePosition position)
        {
            if (region != self.Region || !grid.HasMeleeLine(self.Position, position)) return;
            long dx = (long)position.X - self.Position.X, dy = (long)position.Y - self.Position.Y;
            long distance = dx * dx + dy * dy;
            if (distance < nearest) { nearest = distance; target = id; }
        }
        var npc = world.QuestGiver; Consider(npc.Id, npc.Region, npc.Position);
        foreach (var portal in world.Portals) Consider(portal.Id, portal.Region, portal.Position);
        return target;
    }
    internal void PredictMove(long sequence, int x, int y, double? now = null) => prediction.Move(sequence, x, y, now ?? Now);
    internal long PredictionStarts => prediction.Predictions;
    internal void AcceptInput(InputReceipt receipt) => prediction.Accept(receipt);
    internal void CancelPrediction() { prediction.Cancel(); QueueRedraw(); }
    internal DisplayPosition Display(EntityState entity, double now) => entity.Id.Value == state?.Actor
        ? prediction.Position(entity, now) : presentation.Position(entity, now);
    public override void _Process(double delta)
    {
        bool animating = presentation.IsAnimating(Now) || prediction.IsAnimating(Now);
        if (animating || wasAnimating) QueueRedraw();
        wasAnimating = animating;
    }
    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { Pressed: true }) GrabFocus();
        if (@event is InputEventKey key && key.PhysicalKeycode is Key.W or Key.A or Key.S or Key.D or Key.Up or Key.Down or Key.Left or Key.Right) AcceptEvent();
    }
    public override void _Draw()
    {
        float scale = Math.Min(Size.X / grid.Width, Size.Y / grid.Height);
        Vector2 Point(double x, double y) => new Vector2((float)(x - grid.Origin.X), (float)(y - grid.Origin.Y)) / GameSimulation.UnitsPerTile * scale;
        var floor = world is not null && grid.Region.Value == 2 ? new Color(0.16f, 0.18f, 0.24f) : new Color(0.12f, 0.19f, 0.14f);
        for (int y = 0; y < grid.Height; y++) for (int x = 0; x < grid.Width; x++)
            DrawRect(new(x * scale, y * scale, scale - 1, scale - 1), grid.At(x, y) == CollisionCell.Open ? floor : new Color(0.28f, 0.28f, 0.3f));
        if (State is not { } state) return;
        if (world is not null && state.Started)
        {
            var nearby = NearbyInteraction();
            foreach (var portal in world.Portals)
                if (portal.Region == grid.Region)
                {
                    var point = Point(portal.Position.X, portal.Position.Y);
                    DrawArc(point, scale * 0.42f, 0, Mathf.Tau, 24, Colors.MediumPurple, 3);
                    DrawString(ThemeDB.FallbackFont, point + new Vector2(15, 4), (nearby == portal.Id ? "[E] " : "") + "To " + world.GetRegion(portal.Destination).Name, fontSize: 13);
                }
            var npc = world.QuestGiver;
            if (npc.Region == grid.Region)
            {
                var point = Point(npc.Position.X, npc.Position.Y);
                DrawCircle(point, scale * 0.22f, Colors.Goldenrod);
                DrawString(ThemeDB.FallbackFont, point + new Vector2(15, -4), (nearby == npc.Id ? "[E] " : "") + npc.Name, fontSize: 13);
            }
        }
        double now = Now;
        foreach (var item in VisibleGroundItems) DrawCircle(Point(item.Position.X, item.Position.Y), scale * 0.13f, Colors.Gold);
        foreach (var e in VisibleEntities)
        {
            var display = Display(e, now);
            var p = Point(display.X, display.Y);
            DrawCircle(p, scale * 0.28f, !e.IsAlive ? Colors.Gray : e.Kind == EntityKind.Monster ? Colors.IndianRed : e.Id.Value == state.Actor ? Colors.Cyan : Colors.CornflowerBlue);
            DrawString(ThemeDB.FallbackFont, p + new Vector2(-10, -12), $"{e.Id.Value}:{e.Health}", fontSize: 13);
        }
    }
}
