using System.Net;
using OpenD2.Online;

internal static class RoomInviteContracts
{
    internal static async Task Run(string url, string ca, string mode)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var stop = deadline.Token;
        string password = Guid.NewGuid().ToString("N");
        using var host = new OnlineClient(url, ca, mode);
        await host.Login(new("roominvitehost", password), true, stop);
        var owner = await host.Send<CharacterInfo>(HttpMethod.Post, "v1/characters", new NameRequest("InviteHost"), stop);
        var room = await host.Send<RoomView>(HttpMethod.Post, "v1/rooms", new RoomRequest(owner.Id, "InvitedRoom", password), stop);
        var invite = ConnectionInvite.Parse(new ConnectionInvite { Version = 2, Address = url, Mode = mode,
            RootCaSha256 = host.TrustedRootSha256, Room = room.Id }.Encode());
        if (invite.Encode().Contains(password, StringComparison.Ordinal)) throw new Exception("Room password leaked into invite.");
        using var guest = invite.CreateClient(ca);
        string path = $"v1/rooms/{invite.Room}";
        async Task Reject(HttpStatusCode code, Func<Task> action)
        {
            try { await action(); }
            catch (HttpRequestException error) when (error.StatusCode == code) { return; }
            throw new Exception("Room invite bypassed server admission: " + code);
        }
        await Reject(HttpStatusCode.Unauthorized, async () => await guest.Send<RoomView>(HttpMethod.Post, path + "/join", new JoinRequest(owner.Id, password), stop));
        await guest.Login(new("roominviteguest", password), true, stop);
        var character = await guest.Send<CharacterInfo>(HttpMethod.Post, "v1/characters", new NameRequest("InviteGuest"), stop);
        await Reject(HttpStatusCode.NotFound, async () => await guest.Send<RoomView>(HttpMethod.Post, path + "/join", new JoinRequest(owner.Id, password), stop));
        await Reject(HttpStatusCode.Forbidden, async () => await guest.Send<RoomView>(HttpMethod.Post, path + "/join", new JoinRequest(character.Id, ""), stop));
        var revoked = await host.Send<RoomInvitation>(HttpMethod.Post, path + "/invitation", cancellation: stop);
        await host.Send<object>(HttpMethod.Delete, path + "/invitation", cancellation: stop);
        await Reject(HttpStatusCode.Forbidden, async () => await guest.Send<RoomView>(HttpMethod.Post, path + "/join", new JoinRequest(character.Id, "", revoked.Token), stop));
        var issued = await host.Send<RoomInvitation>(HttpMethod.Post, path + "/invitation", cancellation: stop);
        var timed = ConnectionInvite.Parse((invite with { Version = 3, Invitation = issued.Token, ExpiresAt = issued.ExpiresAt }).Encode());
        var joined = await guest.Send<RoomView>(HttpMethod.Post, path + "/join", new JoinRequest(character.Id, "", timed.Invitation), stop);
        if (joined.Id != room.Id || joined.Actor != 2 || joined.Members.Length != 2) throw new Exception("Invite joined the wrong room.");
        var reconnect = await guest.Send<RoomView>(HttpMethod.Post, path + "/join", new JoinRequest(character.Id, ""), stop);
        if (reconnect.Actor != joined.Actor || reconnect.Members.Length != 2) throw new Exception("Invite reconnect duplicated membership.");
        using var third = invite.CreateClient(ca);
        using var fourth = invite.CreateClient(ca);
        using var outsider = invite.CreateClient(ca);
        var extra = new[] { third, fourth, outsider };
        CharacterInfo? late = null;
        for (int i = 0; i < extra.Length; i++)
        {
            await extra[i].Login(new("roominviteextra" + i, password), true, stop);
            var added = await extra[i].Send<CharacterInfo>(HttpMethod.Post, "v1/characters", new NameRequest("InviteExtra" + i), stop);
            if (i < 2) await extra[i].Send<RoomView>(HttpMethod.Post, path + "/join", new JoinRequest(added.Id, password), stop);
            else late = added;
        }
        await Reject(HttpStatusCode.Forbidden, async () => await outsider.Send<RoomView>(HttpMethod.Post, path + "/join", new JoinRequest(late!.Id, "", timed.Invitation), stop));
        await Reject(HttpStatusCode.Conflict, async () => await outsider.Send<RoomView>(HttpMethod.Post, path + "/join", new JoinRequest(late!.Id, password), stop));
        await third.Send<object>(HttpMethod.Post, path + "/leave", cancellation: stop);
        await fourth.Send<object>(HttpMethod.Post, path + "/leave", cancellation: stop);
        await host.Send<RoomView>(HttpMethod.Post, path + "/start", cancellation: stop);
        await Reject(HttpStatusCode.Conflict, async () => await outsider.Send<RoomView>(HttpMethod.Post, path + "/join", new JoinRequest(late!.Id, password), stop));
        await host.Send<object>(HttpMethod.Post, path + "/close", cancellation: stop);
        await Reject(HttpStatusCode.NotFound, async () => await guest.Send<RoomView>(HttpMethod.Post, path + "/join", new JoinRequest(character.Id, password), stop));
        Console.WriteLine("ROOM INVITE PASS " + mode + " TLS timed-issue/revoke/consume/replay and join/reconnect; anonymous/ownership/password/full/started/closed admission checks retained");
    }
}
