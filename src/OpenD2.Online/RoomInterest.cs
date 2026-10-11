using OpenD2.Core;

namespace OpenD2.Online;

// v3 retains the v2 frame envelope, but entities/items are a recipient-specific projection.
// StateHash remains the opaque hash of the complete authoritative simulation.
public static class RoomInterest
{
    public const string Protocol = "opend2.room.v3";
    public const int Radius = 12 * GameSimulation.UnitsPerTile;

    public const int ExitRadius = 14 * GameSimulation.UnitsPerTile;

    public static RoomView Project(RoomView state, RoomView? previous = null)
    {
        if (!state.Started) return state;
        var self = state.Entities.Single(e => e.Id.Value == state.Actor);
        // The last successfully sent view belongs to this connection only. Never carry
        // visibility across a reconnect, room/actor change, region change or tick rewind.
        bool retain = previous is { Started: true } && previous.Id == state.Id && previous.Actor == state.Actor &&
            previous.Tick <= state.Tick && previous.Entities.Any(e => e.Id == self.Id && e.Region == self.Region);
        var actors = retain ? previous!.Entities.Select(e => e.Id).ToHashSet() : [];
        var ground = retain ? previous!.Items.Where(i => i.Location == ItemLocation.Ground).Select(i => i.Id).ToHashSet() : [];
        bool Nearby(RegionId region, GamePosition position, bool visible) => region == self.Region &&
            Math.Abs((long)position.X - self.Position.X) <= (visible ? ExitRadius : Radius) &&
            Math.Abs((long)position.Y - self.Position.Y) <= (visible ? ExitRadius : Radius);
        // Party state remains available for team UI, including death and region changes.
        var party = state.Members.Select(m => m.Actor).ToHashSet();
        var entities = state.Entities.Where(e => party.Contains(e.Id.Value) || Nearby(e.Region, e.Position, actors.Contains(e.Id))).ToArray();
        var owners = entities.Select(e => e.Id).ToHashSet();
        var items = state.Items.Where(i => i.Location == ItemLocation.Ground
            ? Nearby(i.Region, i.Position, ground.Contains(i.Id)) : owners.Contains(i.Owner)).ToArray();
        return state with { Entities = entities, Items = items };
    }
}
