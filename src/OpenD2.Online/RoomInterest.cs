using OpenD2.Core;

namespace OpenD2.Online;

// v3 retains the v2 frame envelope, but entities/items are a recipient-specific projection.
// StateHash remains the opaque hash of the complete authoritative simulation.
public static class RoomInterest
{
    public const string Protocol = "opend2.room.v3";
    public const int Radius = 12 * GameSimulation.UnitsPerTile;

    public static RoomView Project(RoomView state)
    {
        if (!state.Started) return state;
        var self = state.Entities.Single(e => e.Id.Value == state.Actor);
        bool Nearby(RegionId region, GamePosition position) => region == self.Region &&
            Math.Abs((long)position.X - self.Position.X) <= Radius && Math.Abs((long)position.Y - self.Position.Y) <= Radius;
        // Party state remains available for team UI, including death and region changes.
        var party = state.Members.Select(m => m.Actor).ToHashSet();
        var entities = state.Entities.Where(e => party.Contains(e.Id.Value) || Nearby(e.Region, e.Position)).ToArray();
        var owners = entities.Select(e => e.Id).ToHashSet();
        var items = state.Items.Where(i => i.Location == ItemLocation.Ground
            ? Nearby(i.Region, i.Position) : owners.Contains(i.Owner)).ToArray();
        return state with { Entities = entities, Items = items };
    }
}
