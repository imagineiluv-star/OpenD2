using Godot;
using OpenD2.Core;
using OpenD2.Online;
using System.Net.Http;

namespace OpenD2.Client;

public partial class OnlinePanel
{
    // Two independent Godot processes use the same transport and view binding as the UI.
    // This tests networking in Godot, not visible input/rendering acceptance.
    internal async Task CheckOnline(string url, string role, string run, string evidence)
    {
        busy = true;
        try
        {
            address.Text = url; username.Text = role + run; password.Text = Guid.NewGuid().ToString("N");
            string secret = password.Text;
            await Login(true);
            var character = await client!.Send<CharacterInfo>(HttpMethod.Post, "v1/characters", new NameRequest(role + "Hero"), lifetime.Token);
            await Refresh();
            characters.Select(Array.FindIndex(characterList, c => c.Id == character.Id));
            if (SelectedCharacter() != character.Id) throw new InvalidDataException("Character selection binding failed.");
            string roomTitle = "test" + run;
            if (role == "host") Apply(await client.Send<RoomView>(HttpMethod.Post, "v1/rooms", new RoomRequest(character.Id, roomTitle, ""), lifetime.Token));
            else
            {
                RoomInfo? found = null;
                for (int attempt = 0; attempt < 100 && found is null; attempt++)
                {
                    found = (await client.Send<RoomInfo[]>(HttpMethod.Get, "v1/rooms", cancellation: lifetime.Token)).FirstOrDefault(r => r.Name == roomTitle);
                    if (found is null) await Task.Delay(100, lifetime.Token);
                }
                if (found is null) throw new InvalidDataException("Host room missing.");
                Apply(await client.Send<RoomView>(HttpMethod.Post, $"v1/rooms/{found.Id}/join", new JoinRequest(character.Id, ""), lifetime.Token));
            }
            bool started = false;
            for (int attempt = 0; attempt < 100; attempt++)
            {
                Apply(await client.Send<RoomView>(HttpMethod.Get, $"v1/rooms/{state!.Id}", cancellation: lifetime.Token));
                if (role == "host" && state.Members.Length == 2 && !state.Started) Apply(await Room<RoomView>("start"));
                if (state.Started) { started = true; break; }
                await Task.Delay(100, lifetime.Token);
            }
            if (!started || state!.Members.Length != 2 || state.Entities.Length != 4 || details.Text.Length == 0 || arena.State != state) throw new InvalidDataException("Online view/room start failed.");
            await Refresh();
            rooms.Select(Array.FindIndex(roomList, r => r.Id == state.Id));
            // Host moves away from the monster; guest must see that authoritative movement.
            if (role == "host")
            {
                await InputCommand(CommandKind.SetMove, -1, 0);
                await Task.Delay(240, lifetime.Token);
                await InputCommand(CommandKind.SetMove);
                await Room<object>("save");
            }
            bool observed = false;
            for (int attempt = 0; attempt < 100; attempt++)
            {
                Apply(await client.Send<RoomView>(HttpMethod.Get, $"v1/rooms/{state.Id}", cancellation: lifetime.Token));
                if (state.Entities.Single(e => e.Id.Value == 1).Position.X < 768) { observed = true; break; }
                await Task.Delay(100, lifetime.Token);
            }
            if (!observed) throw new InvalidDataException("Peer movement was not replicated.");
            bool handled = false;
            UserAction(() => { handled = true; return Task.CompletedTask; });
            if (handled || requests.Count != 1) throw new InvalidDataException("Busy user action was not queued.");
            busy = false;
            await Run(() => Task.CompletedTask);
            if (!handled || requests.Count != 0) throw new InvalidDataException("Queued user action was lost.");
            busy = true;
            GD.Print("OPEND2_ONLINE_TRANSPORT_PASS " + role + " GUI_NOT_RUN");
            await CheckGameplay(role, secret, character.Id, evidence);
        }
        finally { busy = false; }
    }
}
