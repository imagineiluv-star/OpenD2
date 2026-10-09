using OpenD2.Assets;

namespace OpenD2.Client;

internal static class ItemDefinitionText
{
	public static string Describe(ItemBaseDefinition item)
	{
		var s = item.Stats;
		return $"Original reference: {item.Name} [{item.Code}] · {item.Kind}\nSize {item.Width}×{item.Height} · type {item.Type}/{item.Type2} · slots {item.BodyLoc1}/{item.BodyLoc2} · stackable {item.Stackable}\n" +
			$"Base damage {s.MinimumDamage}–{s.MaximumDamage}; two-hand {s.TwoHandMinimum}–{s.TwoHandMaximum}; thrown {s.ThrowMinimum}–{s.ThrowMaximum}; defense {s.MinimumDefense}–{s.MaximumDefense}\n" +
			$"Requirements: level {s.RequiredLevel}, STR {s.RequiredStrength}, DEX {s.RequiredDexterity}\nName key: {item.NameKey} · {item.Source}:{item.Line}\nReference only: these values do not change preview combat or bag occupancy.";
	}
}
