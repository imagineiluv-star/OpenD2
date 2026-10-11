using System.Text.Json;
using OpenD2.Core;
using OpenD2.Online;

internal static class RoomInterestContracts
{
    private static void Check(bool yes, string reason) { if (!yes) throw new Exception("Room interest: " + reason); }
    internal static void Run()
    {
        int radius = RoomInterest.Radius;
        var entities = new EntityState[] {
            new(new(1), new(1), new(0, 0)),
            new(new(2), new(2), new(int.MaxValue, int.MinValue)),
            new(new(100), new(1), new(radius, radius), Kind: EntityKind.Monster),
            new(new(101), new(1), new(radius + 1, 0), Kind: EntityKind.Monster),
            new(new(102), new(2), new(0, 0), Kind: EntityKind.Monster),
            new(new(103), new(1), new(-radius, -radius), Kind: EntityKind.Monster, Health: 0) };
        var items = new ItemState[] {
            new(new(1), ItemDefinition.HealthPotion, ItemLocation.Inventory, new(1), 0, new(2), default),
            new(new(2), ItemDefinition.ManaPotion, ItemLocation.Consumed, new(2), 0, new(2), default),
            new(new(3), ItemDefinition.HealthPotion, ItemLocation.Ground, default, -1, new(1), new(radius, 0)),
            new(new(4), ItemDefinition.HealthPotion, ItemLocation.Ground, default, -1, new(1), new(radius + 1, 0)),
            new(new(5), ItemDefinition.HealthPotion, ItemLocation.Ground, default, -1, new(2), default) };
        var original = new RoomView(Guid.NewGuid(), "Interest", true, true, 1, 1, 0, new string('a', 64),
            [new(Guid.NewGuid(), "Host", 1), new(Guid.NewGuid(), "Guest", 2)], entities, items);
        string untouched = JsonSerializer.Serialize(original);
        var projected = RoomInterest.Project(original);
        Check(projected.Entities.Select(e => e.Id.Value).SequenceEqual(new uint[] { 1, 2, 100, 103 }), "inclusive square/region/party/dead actors");
        Check(projected.Items.Select(i => i.Id.Value).SequenceEqual(new ulong[] { 1, 2, 3 }), "owned items and ground radius");
        Check(projected.StateHash == original.StateHash && projected.Members == original.Members && projected.NextSequence == 1, "authoritative metadata");
        Check(RoomInterest.Project(original with { Started = false }).Entities == entities, "lobby unchanged");
        var peer = RoomInterest.Project(original with { Actor = 2, Host = false });
        Check(peer.Entities.Select(e => e.Id.Value).SequenceEqual(new uint[] { 1, 2 }) && peer.Items.Length == 2, "recipient-specific view and overflow-safe distance");
        RoomView? previous = null, decoded = null; long sent = 0, fullBytes = 0;
        for (int i = 1; i <= 600; i++)
        {
            var current = original with { Tick = i, NextSequence = i,
                Entities = entities.Select(e => e.Id.Value == 1 ? e with { Position = new(i % 4 == 0 ? 0 : 2, 0), Region = new(i % 30 == 0 ? 2u : 1u) } : e).ToArray() };
            var visible = RoomInterest.Project(current);
            var frame = RoomFrames.Create(visible, previous, i);
            bool membershipChanged = previous is not null && (!visible.Entities.Select(e => e.Id).SequenceEqual(previous.Entities.Select(e => e.Id)) ||
                !visible.Items.Select(e => e.Id).SequenceEqual(previous.Items.Select(e => e.Id)));
            if (membershipChanged) Check(frame.State is not null, "entry/exit/region changes reset baseline");
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(frame);
            decoded = RoomFrames.Apply(JsonSerializer.Deserialize<RoomFrame>(bytes)!, decoded, i - 1, original.Id);
            Check(decoded.Entities.SequenceEqual(visible.Entities) && decoded.Items.SequenceEqual(visible.Items) && decoded.StateHash == current.StateHash, "exact reconstruction");
            sent += bytes.Length; fullBytes += JsonSerializer.SerializeToUtf8Bytes(new RoomFrame(i, State: current)).Length;
            previous = visible;
        }
        Check(sent < fullBytes, "synthetic serialized payload reduction");
        var reconnect = RoomFrames.Apply(RoomFrames.Create(projected, null, 1), null, 0, original.Id);
        Check(reconnect.Entities.SequenceEqual(projected.Entities) && reconnect.Items.SequenceEqual(projected.Items), "reconnect full baseline");
        Check(JsonSerializer.Serialize(original) == untouched, "source remains unchanged");
        // Published filtered arrays may be replaced but never mutate the source arrays.
        projected.Entities[0] = default; projected.Items[0] = default;
        Check(JsonSerializer.Serialize(original) == untouched, "projection arrays independent");
        Console.WriteLine($"ROOM INTEREST PASS states=600 filtered-json={sent}B full-json={fullBytes}B boundary/region/party/items/overflow/entry-exit/reconnect/immutability");
    }
}
