namespace OpenD2.Core;

public sealed partial class GameSimulation
{
	private static long DistanceSquared(GamePosition a, GamePosition b)
	{ long x = (long)a.X - b.X, y = (long)a.Y - b.Y; return x * x + y * y; }
	private static bool Overlaps(GamePosition a, GamePosition b) => Math.Abs((long)a.X - b.X) < CollisionGrid.BodyRadius * 2 && Math.Abs((long)a.Y - b.Y) < CollisionGrid.BodyRadius * 2;
	private bool CanOccupy(int index, GamePosition position)
	{
		if (!Collision!.CanOccupy(position)) return false;
		for (int i = 0; i < entities.Length; i++)
			if (i != index && entities[i].Region == entities[index].Region && entities[i].IsAlive && Overlaps(position, entities[i].Position)) return false;
		return true;
	}
	private void Emit(long tick, SimulationEventKind kind, EntityState actor, GamePosition to, int value = 0, EntityId target = default)
	{ events[eventCount++] = new(tick, kind, actor.Id, actor.Region, actor.Position, to, value, target); }
	private void Think(long tick)
	{
		int navigationBudget = 512;
		for (int i = 0; i < entities.Length; i++)
		{
			var e = entities[i]; if (e.Kind != EntityKind.Monster || !e.IsAlive || !canAct[i]) continue;
			int target = -1; long nearest = (long)AggroRange * AggroRange + 1;
			for (int j = 0; j < entities.Length; j++)
			{
				var candidate = entities[j]; if (candidate.Kind != EntityKind.Player || !candidate.IsAlive || candidate.Region != e.Region) continue;
				long distance = DistanceSquared(e.Position, candidate.Position);
				if (distance < nearest) { nearest = distance; target = j; } // sorted IDs break ties
			}
			EntityId id = target < 0 ? default : entities[target].Id;
			var mode = target < 0 ? MonsterMode.Idle : nearest <= (long)AttackRange * AttackRange && Collision!.HasMeleeLine(e.Position, entities[target].Position) ? MonsterMode.Attacking : MonsterMode.Chasing;
			int x = mode == MonsterMode.Chasing ? Math.Sign(entities[target].Position.X - e.Position.X) : 0;
			int y = mode == MonsterMode.Chasing ? Math.Sign(entities[target].Position.Y - e.Position.Y) : 0;
			if (mode == MonsterMode.Chasing && World?.NavigateWalls == true && !GridPathfinder.HasWalkLine(Collision!, e.Position, entities[target].Position))
			{
				x = y = 0;
				if (navigationBudget > 0)
				{
					var route = GridPathfinder.Find(Collision!, e.Position, entities[target].Position, navigationBudget);
					navigationBudget -= Math.Max(1, route.Expanded);
					if (route.Status == PathStatus.Found && route.Points.Count > 0)
					{ x = Math.Sign(route.Points[0].X - e.Position.X); y = Math.Sign(route.Points[0].Y - e.Position.Y); }
				}
			}
			entities[i] = e with { Target = id, Mode = mode, MoveX = x, MoveY = y };
			if (e.Mode != mode || e.Target != id) Emit(tick, SimulationEventKind.MonsterChanged, e, e.Position, (int)mode, id);
			if (mode == MonsterMode.Attacking && e.AttackCooldown == 0) attacks[i] = id;
		}
	}
	private void Attack(int index, long tick)
	{
		var e = entities[index]; int targetIndex = indices[attacks[index]]; var target = entities[targetIndex];
		AttackFailure? failure = !canAct[index] ? AttackFailure.Interrupted : e.AttackCooldown > 0 ? AttackFailure.Cooldown :
			!target.IsAlive ? AttackFailure.DeadTarget : DistanceSquared(e.Position, target.Position) > (long)AttackRange * AttackRange ? AttackFailure.OutOfRange :
			!Collision!.HasMeleeLine(e.Position, target.Position) ? AttackFailure.Obstructed : null;
		bool skill = skillAttacks[index];
		if (skill)
		{
			SkillFailure? skillFailure = failure is { } combatFailure ? (SkillFailure)(int)combatFailure :
				castSkills[index] == SkillId.None ? SkillFailure.NoSkill : e.SkillCooldown > 0 ? SkillFailure.Cooldown : e.Mana < PowerStrikeManaCost ? SkillFailure.InsufficientMana : null;
			if (skillFailure is { } reason) { Emit(tick, SimulationEventKind.SkillFailed, e, target.Position, (int)reason, target.Id); return; }
		}
		else if (failure is { } reason) { Emit(tick, SimulationEventKind.AttackFailed, e, target.Position, (int)reason, target.Id); return; }
		var stats = GetStats(e.Id);
		int damage = Math.Max(1, stats.MinimumDamage + random.NextInt(stats.MaximumDamage - stats.MinimumDamage + 1) + (skill ? PowerStrikeBonusDamage : 0) - GetStats(target.Id).Armor);
		int health = Math.Max(0, target.Health - damage);
		entities[index] = e with { AttackCooldown = e.Kind == EntityKind.Player ? PlayerAttackInterval : MonsterAttackInterval,
			Mana = skill ? e.Mana - PowerStrikeManaCost : e.Mana, SkillCooldown = skill ? PowerStrikeCooldownTicks : e.SkillCooldown,
			ManaRecoveryTicks = skill ? 0 : e.ManaRecoveryTicks };
		attacked[index] = true;
		entities[targetIndex] = target with { Health = health, HitStun = health == 0 ? 0 : HitStunTicks,
			MoveX = health == 0 ? 0 : target.MoveX, MoveY = health == 0 ? 0 : target.MoveY,
			Mode = health == 0 ? MonsterMode.Dead : target.Mode, Target = health == 0 ? default : target.Target };
		canAct[targetIndex] = false;
		Emit(tick, skill ? SimulationEventKind.SkillCast : SimulationEventKind.AttackStarted, e, target.Position, skill ? (int)castSkills[index] : 0, target.Id);
		Emit(tick, SimulationEventKind.Hit, e, target.Position, Math.Min(damage, target.Health), target.Id);
		if (health == 0)
		{
			Emit(tick, SimulationEventKind.Died, target, target.Position, 0, e.Id);
			DropLoot(target, tick);
			UpdateQuest(tick);
		}
	}
}
