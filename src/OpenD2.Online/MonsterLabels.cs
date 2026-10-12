using OpenD2.Core;

namespace OpenD2.Online;

// Text for the living monsters in the viewer's region ("Preview grunt Lv 1 60/60"). Presentation only: it reads a room view and
// never touches the simulation. Kinds the table does not know are shown without a name or level instead of being hidden.
public static class MonsterLabels
{
    public const int MaxListed = 8;
    public static string Describe(RoomView view, MonsterRoster roster, IReadOnlyCollection<MonsterInfo> kinds)
    {
        ArgumentNullException.ThrowIfNull(view); ArgumentNullException.ThrowIfNull(roster); ArgumentNullException.ThrowIfNull(kinds);
        var me = view.Entities.FirstOrDefault(e => e.Id.Value == view.Actor && e.Kind == EntityKind.Player);
        if (me.Id.Value == 0) return "";
        var lines = view.Entities.Where(e => e.Kind == EntityKind.Monster && e.IsAlive && e.Region == me.Region).OrderBy(e => e.Id.Value).Take(MaxListed)
            .Select(e =>
            {
                var kind = roster.KindOf(e.Id) is { } code ? kinds.FirstOrDefault(k => string.Equals(k.Code, code, StringComparison.OrdinalIgnoreCase)) : null;
                return kind is null ? $"Monster {e.Id.Value} {e.Health}/{e.MaxHealth}" : $"{kind.Name} Lv {kind.Level} {e.Health}/{e.MaxHealth}";
            });
        return string.Join(" · ", lines);
    }
}
