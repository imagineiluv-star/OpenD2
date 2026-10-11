using System.Security.Cryptography;
using OpenD2.Online;

namespace OpenD2.Server;

public sealed partial class Realm
{
    // One outstanding capability per room, held only in memory. Restart, host
    // transfer, start and room closure invalidate it. No plaintext token is stored.
    public RoomInvitation IssueInvitation(string token, Guid id)
    {
        lock (gate)
        {
            var (room, character) = Access(token, id);
            Need(room.Saved.Host == character.Id, 403, "Only the host can issue invitations.");
            Need(room.Game is null && room.Saved.Members.Length < 4, 409, "Room is started or full.");
            byte[] secret = RandomNumberGenerator.GetBytes(32);
            room.InviteHash = SHA256.HashData(secret);
            room.InviteUntil = clock.GetUtcNow().AddMinutes(10);
            return new(Convert.ToHexString(secret), room.InviteUntil);
        }
    }
    public object RevokeInvitation(string token, Guid id)
    {
        lock (gate)
        {
            var (room, character) = Access(token, id);
            Need(room.Saved.Host == character.Id, 403, "Only the host can revoke invitations.");
            room.InviteHash = null;
            return new { ok = true };
        }
    }
    private void ValidateInvitation(Room room, string invitation)
    {
        Need(invitation.Length == 64 && invitation.All(Uri.IsHexDigit), 403, "Invitation is invalid, expired or already used.");
        Need(room.InviteHash is not null && room.InviteUntil > clock.GetUtcNow() &&
            CryptographicOperations.FixedTimeEquals(room.InviteHash, SHA256.HashData(Convert.FromHexString(invitation))),
            403, "Invitation is invalid, expired or already used.");
    }
}
