using Godot;
using OpenD2.Core;
using OpenD2.Online;
using System.Net.Http;

namespace OpenD2.Client;

public partial class OnlinePanel
{
    private readonly OptionButton worldChoice = new() { Disabled = true };
    private WorldContent? SelectedWorld => worldChoice.Selected == 1 ? CooperativeWorld.Content : null;

    private async Task RefreshWorlds()
    {
        var selected = SelectedWorld;
        var available = client!.SupportsWorldRooms
            ? await client.Send<WorldContent[]>(HttpMethod.Get, "v1/worlds", cancellation: lifetime.Token) : [];
        worldChoice.Clear(); worldChoice.AddItem("Arena");
        if (available.Contains(CooperativeWorld.Content)) worldChoice.AddItem("Cooperative world — Camp / Field (preview)");
        worldChoice.Select(selected is not null && worldChoice.ItemCount > 1 ? 1 : 0);
        worldChoice.Disabled = state is not null;
    }

    private async Task CreateRoom()
    {
        RequireLogin();
        Apply(await client!.Send<RoomView>(HttpMethod.Post, "v1/rooms",
            new RoomRequest(SelectedCharacter(), roomName.Text, roomPassword.Text, SelectedWorld), lifetime.Token));
    }

    private async Task JoinSelectedRoom()
    {
        RequireLogin();
        if (rooms.Selected < 0 || rooms.Selected >= roomList.Length) throw new InvalidOperationException("Select a room.");
        var room = roomList[rooms.Selected];
        ValidateWorld(room.World);
        Apply(await client!.Send<RoomView>(HttpMethod.Post, $"v1/rooms/{room.Id}/join",
            new JoinRequest(SelectedCharacter(), roomPassword.Text, World: room.World), lifetime.Token));
    }

    private static void ValidateWorld(WorldContent? content)
    {
        if (content is not null) CooperativeWorld.Resolve(content);
    }

    private string RoomsPath => client!.SupportsWorldRooms ? "v1/rooms?includeWorlds=true" : "v1/rooms";
    private static string WorldTitle(WorldContent? content) => content is null ? "Arena" :
        content == CooperativeWorld.Content ? "Camp / Field" : "World — matching build required";
    private static string RegionName(EntityState entity) => CooperativeWorld.Definition.GetRegion(entity.Region).Name;

    private static InputRequest MakeInput(RoomView view, long sequence, CommandKind kind, int x = 0, int y = 0, uint target = 0, ulong item = 0) =>
        new(sequence, kind, x, y, target, item,
            view.World is null ? null : view.Entities.Single(e => e.Id.Value == view.Actor).Region.Value, view.World?.RegionEpoch);

    private Task InteractNearby()
    {
        var target = arena.NearbyInteraction();
        if (target.Value == 0) { status.Text = "Move closer to a scout or portal, then press E."; return Task.CompletedTask; }
        return InputAction(CommandKind.Interact, target: target.Value);
    }
}
