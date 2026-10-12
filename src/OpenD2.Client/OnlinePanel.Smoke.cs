using Godot;
using OpenD2.Core;
using OpenD2.Online;
using System.Net.Http;

namespace OpenD2.Client;

public partial class OnlinePanel
{
    // Two independent Godot processes use the same transport and view binding as the UI.
    // This tests networking in Godot, not visible input/rendering acceptance.
    internal async Task CheckOnline(string url, string role, string run, string evidence, string? certificate = null, string mode = "realm")
    {
        await Run(() => throw new HttpRequestException(HttpRequestError.SecureConnectionError, "private transport detail"));
        if (!status.Text.Contains("certificate SAN", StringComparison.Ordinal) || status.Text.Contains("private transport detail", StringComparison.Ordinal))
            throw new InvalidDataException("Connection guidance UI binding failed.");
        GD.Print("OPEND2_CONNECTION_GUIDANCE_UI_PASS");
        CheckInputReceiptOrdering();
        CheckRoomPresentation();
        CheckRoomPrediction();
        busy = true;
        try
        {
            if (mode is not ("realm" or "open")) throw new InvalidDataException("Unknown smoke mode.");
            if (!string.IsNullOrEmpty(certificate)) await CheckPublishedCatalog(url, certificate, mode, role, evidence);
            serverMode.Select(mode == "open" ? 1 : 0);
            caFile = certificate ?? "";
            if (!string.IsNullOrEmpty(certificate)) await CheckInviteConnection(url, certificate, mode, role, evidence);
            address.Text = url; username.Text = role + run; password.Text = Guid.NewGuid().ToString("N");
            string secret = password.Text;
            await Login(true);
            Acceptance("login");
            if (!client!.SupportsInputReceipts) throw new InvalidDataException("Packaged server did not advertise input receipts.");
            var character = await client!.Send<CharacterInfo>(HttpMethod.Post, "v1/characters", new NameRequest(role + "Hero"), lifetime.Token);
            await Refresh();
            characters.Select(Array.FindIndex(characterList, c => c.Id == character.Id));
            if (SelectedCharacter() != character.Id) throw new InvalidDataException("Character selection binding failed.");
            Acceptance("character_selected");
            if (!serverMode.Disabled || client.Mode != mode) throw new InvalidDataException("Mode selection binding failed.");
            if (mode == "open") await CheckProfileFiles(character);
            string roomTitle = "test" + run;
            if (role == "host")
            {
                Apply(await client.Send<RoomView>(HttpMethod.Post, "v1/rooms", new RoomRequest(character.Id, roomTitle, ""), lifetime.Token));
                if (!string.IsNullOrEmpty(certificate))
                {
                    PublishInvite(evidence, "room-invite.json", CurrentRoomInvite());
                    Acceptance("room_invite_generated");
                }
            }
            else if (!string.IsNullOrEmpty(certificate))
            {
                string text = await ReceiveInvite(evidence, "room-invite.json");
                var invite = ConnectionInvite.Parse(text);
                if (new Uri(invite.Address) != new Uri(url) || invite.Mode != mode || invite.Version != 2)
                    throw new InvalidDataException("Room invite changed the test destination or mode.");
                Reset(); ApplyInvite(text); caFile = certificate; password.Text = secret;
                await Login(false);
                characters.Select(Array.FindIndex(characterList, c => c.Id == character.Id));
                await JoinInvitedRoom();
                if (state is null || state.Id != invite.Room || state.Host || state.Actor != 2)
                    throw new InvalidDataException("Invited guest did not join the host room with its own actor.");
                Acceptance("invited_room_joined");
            }
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
                await WaitForRoomUpdate();
                if (role == "host" && state!.Members.Length == 2 && !state.Started)
                {
                    Apply(await Room<RoomView>("start"));
                    continue; // The HTTP response is full; validate the next negotiated WSS view.
                }
                if (state!.Started) { started = true; break; }
                await Task.Delay(100, lifetime.Token);
            }
            if (!started || state!.Members.Length != 2 || !state.Entities.Select(e => e.Id.Value).Order().SequenceEqual(new uint[] { 1, 2, 100 }) || details.Text.Length == 0 || arena.State != state) throw new InvalidDataException("Online view/room start failed.");
            Acceptance("same_room");
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
                await WaitForRoomUpdate();
                if (state.Entities.Single(e => e.Id.Value == 1).Position.X < 768) { observed = true; break; }
                await Task.Delay(100, lifetime.Token);
            }
            if (!observed) throw new InvalidDataException("Peer movement was not replicated.");
            // Bursty/slow delivery may correctly snap every sample; counts are diagnostic,
            // while the deterministic UI probe above verifies the actual display path.
            GD.Print($"OPEND2_ROOM_PRESENTATION_LIVE {role} transitions={arena.PresentationTransitions}");
            GD.Print($"OPEND2_ROOM_PREDICTION_LIVE {role} starts={arena.PredictionStarts}");
            GD.Print("OPEND2_ONLINE_STREAM_PASS " + role);
            // Observe actual decoded v4 frames, not only the advertised capability.
            for (int attempt = 0; attempt < 100 && client!.RoomDeltaFrames == 0; attempt++) await WaitForRoomUpdate();
            if (!client!.SupportsRoomDeltas || client.RoomFullFrames == 0 || client.RoomDeltaFrames == 0)
                throw new InvalidDataException("Packaged client did not reconstruct a delta stream.");
            GD.Print("OPEND2_ROOM_DELTA_UI_PASS " + role);
            if (!client.SupportsRoomInterest) throw new InvalidDataException("Packaged client did not negotiate interest filtering.");
            GD.Print("OPEND2_ROOM_INTEREST_UI_PASS " + role);
            long progressProbe = nextInputSequence;
            await InputCommand(CommandKind.SetMove);
            for (int attempt = 0; attempt < 100 && !(state?.ProcessedSequence >= progressProbe); attempt++) await WaitForRoomUpdate();
            if (!client.SupportsRoomInputProgress || !(state?.ProcessedSequence >= progressProbe) || state.ProcessedSequence >= state.NextSequence)
                throw new InvalidDataException("Packaged client did not observe consumed inputs over v4.");
            GD.Print("OPEND2_ROOM_INPUT_PROGRESS_UI_PASS " + role);
            bool handled = false;
            UserAction(() => { handled = true; return Task.CompletedTask; });
            if (handled || requests.Count != 1) throw new InvalidDataException("Busy user action was not queued.");
            busy = false;
            await Run(() => Task.CompletedTask);
            if (!handled || requests.Count != 0) throw new InvalidDataException("Queued user action was lost.");
            busy = true;
            GD.Print("OPEND2_ONLINE_TRANSPORT_PASS " + role + " GUI_NOT_RUN");
            await CheckGameplay(role, secret, character.Id, evidence);
            GD.Print("OPEND2_INPUT_RECEIPT_GAMEPLAY_PASS " + role);
            if (mode == "open" && role == "host" && System.Environment.GetEnvironmentVariable("OPEND2_TEST_HOST_EXE") is { Length: > 0 } hostExe)
            {
                Reset();
                await CheckPersonalHostControls(hostExe, Path.Combine(Path.GetTempPath(), "opend2-host-ui-" + run));
                if (System.Environment.GetEnvironmentVariable("OPEND2_TEST_HOST_TLS_FIXTURES") is { Length: > 0 } fixtures)
                    await CheckLanHostControls(hostExe, Path.Combine(Path.GetTempPath(), "opend2-lan-ui-" + run), fixtures);
            }
        }
        finally { busy = false; }
    }
}
