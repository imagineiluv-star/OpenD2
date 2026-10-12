using OpenD2.Core;
using OpenD2.Online;

namespace OpenD2.Server;

public sealed partial class Realm
{
    // Names only, never numbers: "3" must not select a class the caller cannot see by name.
    private static CharacterClass ParseClass(string value)
    {
        Need(value is { Length: >= 1 and <= 16 } && value.All(char.IsAsciiLetter) && Enum.TryParse<CharacterClass>(value, true, out var cls) && Enum.IsDefined(cls), 400, "Unknown character class.");
        return Enum.Parse<CharacterClass>(value, true);
    }
    private bool InRoom(Guid character) => rooms.Values.Any(r => r.Saved.Members.Any(m => m.Character == character));
    // Points are spent between rooms: experience earned in a room is settled when the host closes it, so a spend during the room would race that settlement.
    private (Character Character, int Index) Spendable(string token, Guid id)
    {
        var c = Owned(Authorize(token), id);
        Need(c.Progress is not null, 409, "This character has no class.");
        Need(!InRoom(id), 409, "Leave or close the room before spending points.");
        return (c, characters.IndexOf(c));
    }
    // The server's skill table, so a client never has to assume which tables the server runs on.
    public SkillInfo[] Skills(string token)
    {
        lock (gate)
        {
            Authorize(token);
            return SkillTree.All.OrderBy(s => s.Id).Select(s => new SkillInfo(s.Id, s.Name, s.Class.ToString(), s.RequiredLevel, s.MaxLevel, s.Requires.ToArray())).ToArray();
        }
    }
    public CharacterInfo SpendStat(string token, Guid id, StatRequest request)
    {
        Need(request?.Stat is { Length: >= 1 and <= 16 } name && name.All(char.IsAsciiLetter) && Enum.TryParse<CharacterStat>(name, true, out var stat) && Enum.IsDefined(stat), 400, "Unknown attribute.");
        var chosen = Enum.Parse<CharacterStat>(request!.Stat, true);
        lock (gate)
        {
            var (c, at) = Spendable(token, id);
            try { characters[at] = c with { Progress = Progression.AddStat(c.Progress!, chosen) }; }
            catch (InvalidOperationException error) { throw new Rejected(409, error.Message); }
            Save(); return Info(characters[at]);
        }
    }
    public CharacterInfo SpendSkill(string token, Guid id, SkillRequest request)
    {
        Need(request is not null, 400, "Missing skill.");
        int skill = request!.Skill;
        lock (gate)
        {
            var (c, at) = Spendable(token, id);
            Need(SkillTree.TryGet(skill, out _), 400, "Unknown skill.");
            try { characters[at] = c with { Progress = Progression.AddSkill(SkillTree, c.Progress!, skill) }; }
            catch (InvalidOperationException error) { throw new Rejected(409, error.Message); }
            Save(); return Info(characters[at]);
        }
    }
    // Caller holds gate. A monster's death pays its experience to every living member of the room who stands in the monster's region
    // (a party share without distance or party bonus) and whose character has a class. It is room state until the host closes the room.
    private void AwardExperience(Room room, SimulationEvent death)
    {
        var game = room.Game!; var monster = game.GetEntity(death.Actor);
        if (monster.Kind != EntityKind.Monster) return;
        uint reward = Progression.PreviewKillExperience(monster.MaxHealth);
        if (reward == 0) return;
        room.Saved = room.Saved with { Members = room.Saved.Members.Select(m =>
            characters.Single(c => c.Id == m.Character).Progress is not null && game.GetEntity(new(m.Actor)) is { IsAlive: true } actor && actor.Region == monster.Region
                ? m with { Experience = (uint)Math.Min(uint.MaxValue, (long)m.Experience + reward) } : m).ToArray() };
    }
}
