namespace OpenD2.Core;

public enum SkillId { None, PowerStrike }
// The first five codes deliberately match AttackFailure: both use the same combat gates.
public enum SkillFailure { Cooldown, OutOfRange, Obstructed, DeadTarget, Interrupted, NoSkill, InsufficientMana }

public sealed partial class GameSimulation
{
	public const int PowerStrikeManaCost = 12, PowerStrikeBonusDamage = 12, PowerStrikeCooldownTicks = 25, ManaRecoveryInterval = 25;
	private static bool ValidResources(EntityState e) => e.MaxMana is >= 0 and <= 100000 && e.Mana >= 0 && e.Mana <= e.MaxMana &&
		e.SkillCooldown is >= 0 and <= PowerStrikeCooldownTicks && e.ManaRecoveryTicks is >= 0 and < ManaRecoveryInterval &&
		(e.Mana < e.MaxMana || e.ManaRecoveryTicks == 0) && Enum.IsDefined(e.SelectedSkill);
	private static EntityState AdvanceResources(EntityState e)
	{
		if (!e.IsAlive || e.Kind != EntityKind.Player) return e;
		int mana = e.Mana, recovery = e.ManaRecoveryTicks;
		if (mana < e.MaxMana && ++recovery == ManaRecoveryInterval) { mana++; recovery = 0; }
		return e with { Mana = mana, ManaRecoveryTicks = recovery, SkillCooldown = Math.Max(0, e.SkillCooldown - 1) };
	}
}
