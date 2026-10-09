using System.Text.Json.Nodes;
using OpenD2.Assets;
using OpenD2.Core;

internal static class SkillContracts
{
	private static readonly EntityState Player = new(new(1), new(1), new(384, 384));
	private static readonly EntityState Monster = new(new(2), new(1), new(640, 384), Kind: EntityKind.Monster, AttackCooldown: 20);
	private static CollisionGrid Grid() => new(new(1), 10, 10, Enumerable.Repeat(CollisionCell.Open, 100).ToArray());
	private static GameSimulation Game(EntityState? player = null, EntityState? monster = null) => new(1, [player ?? Player, monster ?? Monster], Grid());
	private static GameCommand Cast(GameSimulation game, ulong sequence = 1) => new(game.Tick + 1, sequence, Player.Id, Player.Region, CommandKind.CastSkill, Target: Monster.Id);
	private static void Check(bool value) { if (!value) throw new Exception("Skill assertion failed."); }
	private static void Throws<T>(Action action) where T : Exception
	{ try { action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}."); }
	private static void Accept(GameSimulation game, GameCommand command) => Check(game.Submit(command) == CommandResult.Accepted);
	private static void Edit(string file, Action<JsonNode> change)
	{ var json = JsonNode.Parse(File.ReadAllText(file))!; change(json); File.WriteAllText(file, json.ToJsonString()); }
	public static void Run(string root, Action<string, Action> test)
	{
		test("power strike shares damage/loot gates and pins independent v7 state", () =>
		{
			var game = Game(monster: Monster with { Health = 1, AttackCooldown = 0 }); Accept(game, Cast(game)); game.Step();
			var player = game.GetEntity(Player.Id);
			Check(player.Mana == 48 && player.SkillCooldown == 25 && player.AttackCooldown == 12 && game.Items.Length == 2);
			Check(game.Events.ToArray().Count(e => e.Kind == SimulationEventKind.SkillCast) == 1 && game.Events.ToArray().Any(e => e.Kind == SimulationEventKind.Died));
			Check(game.ComputeStateHash() == "d88d2b8f709d33b869ded3a8d6de78e701ec16110e7eb8ccc8947f545990d760");
		});
		test("power strike adds exactly twelve physical damage using the same RNG sample", () =>
		{
			var ordinary = Game(); var skill = Game(); Accept(ordinary, Cast(ordinary) with { Kind = CommandKind.Attack }); Accept(skill, Cast(skill)); ordinary.Step(); skill.Step();
			Check(ordinary.GetEntity(Monster.Id).Health - skill.GetEntity(Monster.Id).Health == 12 && ordinary.RandomState == skill.RandomState);
		});
		test("last combat action wins so repeated casts or attack/cast mixtures spend once", () =>
		{
			foreach (var last in new[] { CommandKind.CastSkill, CommandKind.Attack })
			{
				var game = Game(); for (ulong i = 1; i <= 128; i++) Accept(game, Cast(game, i) with { Kind = i == 128 ? last : CommandKind.CastSkill });
				game.Step(); Check(game.GetEntity(Player.Id).Mana == (last == CommandKind.CastSkill ? 48 : 60));
				Check(game.Events.ToArray().Count(e => e.Kind is SimulationEventKind.SkillCast or SimulationEventKind.AttackStarted) == 1);
			}
		});
		test("resource failure neither consumes mana/RNG nor starts a cooldown", () =>
		{
			foreach (var actor in new[] { Player with { Mana = 11 }, Player with { MaxMana = 0, Mana = 0 }, Player with { SelectedSkill = SkillId.None } })
			{
				var game = Game(actor); Accept(game, Cast(game)); game.Step(); var after = game.GetEntity(Player.Id);
				Check(after.Mana == actor.Mana && after.SkillCooldown == 0 && after.AttackCooldown == 0 && game.RandomState == 1);
				Check(game.Events.ToArray().Any(e => e.Kind == SimulationEventKind.SkillFailed && e.Value == (int)(actor.SelectedSkill == SkillId.None ? SkillFailure.NoSkill : SkillFailure.InsufficientMana)));
			}
		});
		test("cooldown and pre-tick stun reject casts without spending", () =>
		{
			foreach (var actor in new[] { Player with { SkillCooldown = 2 }, Player with { AttackCooldown = 2 }, Player with { HitStun = 1 } })
			{
				var game = Game(actor); Accept(game, Cast(game)); game.Step();
				Check(game.GetEntity(Player.Id).Mana == 60 && game.RandomState == 1 && game.Events.ToArray().Any(e => e.Kind == SimulationEventKind.SkillFailed));
			}
			var ready = Game(Player with { SkillCooldown = 1, AttackCooldown = 1 }); Accept(ready, Cast(ready)); ready.Step(); Check(ready.GetEntity(Player.Id).Mana == 48);
		});
		test("same-tick earlier enemy hit interrupts queued skill", () =>
		{
			var player = Player with { Id = new(3) }; var game = Game(player, Monster with { AttackCooldown = 0 });
			Accept(game, Cast(game) with { Actor = player.Id }); game.Step();
			Check(game.GetEntity(player.Id).Mana == 60 && game.GetEntity(player.Id).Health < 100);
			Check(game.Events.ToArray().Any(e => e.Kind == SimulationEventKind.SkillFailed && e.Value == (int)SkillFailure.Interrupted));
		});
		test("range, wall corner and dead-target failures leave resource state unspent", () =>
		{
			var far = Game(monster: Monster with { Position = new(1664, 384) }); Accept(far, Cast(far)); far.Step();
			Check(far.Events.ToArray().Any(e => e.Kind == SimulationEventKind.SkillFailed && e.Value == (int)SkillFailure.OutOfRange));
			var dead = Game(monster: Monster with { Health = 0 }); Accept(dead, Cast(dead)); dead.Step();
			Check(dead.GetEntity(Player.Id).Mana == 60 && dead.RandomState == 1);
			var cells = Enumerable.Repeat(CollisionCell.Open, 100).ToArray(); cells[1] = CollisionCell.Blocked;
			var wall = new GameSimulation(1, [Player with { Position = new(128, 128) }, Monster with { Position = new(384, 384) }], new(new(1), 10, 10, cells));
			Accept(wall, Cast(wall)); wall.Step(); Check(wall.GetEntity(Player.Id).Mana == 60 && wall.RandomState == 1 && wall.Events.ToArray().Any(e => e.Value == (int)SkillFailure.Obstructed && e.Kind == SimulationEventKind.SkillFailed));
		});
		test("mana recovers by simulation ticks, clamps at maximum and freezes when dead", () =>
		{
			var game = new GameSimulation(1, [Player with { Mana = 59, HitStun = 3 }]);
			for (int i = 0; i < 24; i++) game.Step(); Check(game.GetEntity(Player.Id).Mana == 59 && game.GetEntity(Player.Id).ManaRecoveryTicks == 24);
			game.Step(); Check(game.GetEntity(Player.Id).Mana == 60 && game.GetEntity(Player.Id).ManaRecoveryTicks == 0);
			for (int i = 0; i < 40; i++) game.Step(); Check(game.GetEntity(Player.Id).Mana == 60 && game.GetEntity(Player.Id).ManaRecoveryTicks == 0);
			var dead = new GameSimulation(1, [Player with { Health = 0, Mana = 10, ManaRecoveryTicks = 24, SkillCooldown = 5 }]);
			dead.Step(); Check(dead.GetEntity(Player.Id).Mana == 10 && dead.GetEntity(Player.Id).ManaRecoveryTicks == 24 && dead.GetEntity(Player.Id).SkillCooldown == 5);
			Check(dead.Submit(Cast(dead)) == CommandResult.DeadActor);
		});
		test("invalid skill payloads and monster authority fail before queue mutation", () =>
		{
			var game = Game(); string before = game.ComputeStateHash();
			foreach (var command in new[] { Cast(game) with { X = 1 }, Cast(game) with { Item = new(2) }, Cast(game) with { Kind = CommandKind.SelectSkill, X = 2, Target = default }, Cast(game) with { Kind = CommandKind.SelectSkill } })
				Check(game.Submit(command) == CommandResult.InvalidCommand);
			Check(game.Submit(Cast(game) with { Actor = Monster.Id, Target = Player.Id }) == CommandResult.NotPlayerControlled);
			Check(game.Submit(Cast(game) with { Target = Player.Id }) == CommandResult.InvalidTarget && game.ComputeStateHash() == before);
		});
		test("selection is ordered, captured for each cast and cannot bypass stun", () =>
		{
			var game = Game(); Accept(game, Cast(game)); Accept(game, Cast(game, 2) with { Kind = CommandKind.SelectSkill, Target = default, X = 0 }); game.Step();
			Check(game.GetEntity(Player.Id).SelectedSkill == SkillId.None && game.GetEntity(Player.Id).Mana == 48);
			var unselected = Game(); Accept(unselected, Cast(unselected) with { Kind = CommandKind.SelectSkill, Target = default }); Accept(unselected, Cast(unselected, 2)); unselected.Step();
			Check(unselected.GetEntity(Player.Id).Mana == 60 && unselected.RandomState == 1);
			var stunned = Game(Player with { HitStun = 1 }); Accept(stunned, Cast(stunned) with { Kind = CommandKind.SelectSkill, Target = default }); stunned.Step();
			Check(stunned.GetEntity(Player.Id).SelectedSkill == SkillId.PowerStrike && stunned.RandomState == 1);
		});
		test("invalid resources cannot enter initial or restored authoritative state", () =>
		{
			foreach (var actor in new[] { Player with { Mana = -1 }, Player with { Mana = 61 }, Player with { MaxMana = 100001 }, Player with { SkillCooldown = 26 }, Player with { ManaRecoveryTicks = 25 }, Player with { ManaRecoveryTicks = 1 }, Player with { SelectedSkill = (SkillId)99 } })
			{
				Throws<ArgumentException>(() => new GameSimulation(1, [actor]));
				var state = new GameSimulation(1, [Player]).CaptureSnapshot(); Throws<InvalidDataException>(() => GameSimulation.Restore(state with { Entities = [actor] }));
			}
		});
		test("queued selection/cast and fractional recovery survive current save and replay", () =>
		{
			var game = Game(Player with { Mana = 20, ManaRecoveryTicks = 24 }); var baseline = game.CaptureSnapshot(); var selection = Cast(game) with { Kind = CommandKind.SelectSkill, Target = default, X = 1 }; var cast = Cast(game, 2); Accept(game, selection); Accept(game, cast);
			string file = Path.Combine(root, "skill-roundtrip.json"); GameSave.Save(file, game.CaptureSnapshot()); var loaded = GameSave.Load(file, Grid());
			Check(!loaded.Migrated && loaded.Simulation.ComputeStateHash() == game.ComputeStateHash()); game.Step(); loaded.Simulation.Step();
			Check(game.GetEntity(Player.Id).Mana == 9 && loaded.Simulation.ComputeStateHash() == game.ComputeStateHash());
			Check(GameSimulation.Replay(baseline, [new(0, selection), new(0, cast)], 1).ComputeStateHash() == game.ComputeStateHash());
		});
		test("v2 checksum is verified before resource defaults; grid and old queue stay intact", () =>
		{
			string file = Path.Combine(root, "mana-migration.json"); File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "save-v2-items.json"), file); byte[] source = File.ReadAllBytes(file);
			var result = GameSave.Load(file, Grid()); var game = result.Simulation;
			Check(result.Migrated && game.GetEntity(Player.Id).Mana == 60 && game.GetEntity(Player.Id).SelectedSkill == SkillId.PowerStrike && game.PendingCommands == 2);
			Check(File.ReadAllBytes(file).SequenceEqual(source) && !File.Exists(file + ".bak"));
			for (int i = 0; i < 3; i++) game.Step(); Check(game.GetItem(new(2)).Slot == 2 && game.GetItem(new(4)).Slot == 1);
			GameSave.Save(file, game.CaptureSnapshot()); Check(File.ReadAllBytes(file + ".bak").SequenceEqual(source) && !GameSave.Load(file, Grid()).Migrated);
		});
		test("old unchecksummed resource injection and current missing/tampered fields are rejected", () =>
		{
			string fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "save-v2-items.json"); string file = Path.Combine(root, "skill-corrupt.json");
			foreach (string field in new[] { "Mana", "MaxMana", "SkillCooldown", "ManaRecoveryTicks", "SelectedSkill" })
			{
				File.Copy(fixture, file, true); Edit(file, j => j["Entities"]![0]![field] = 0); Throws<InvalidDataException>(() => GameSave.Load(file, Grid()));
				File.Delete(file); GameSave.Save(file, Game().CaptureSnapshot()); Edit(file, j => j["Entities"]![0]!.AsObject().Remove(field)); Throws<InvalidDataException>(() => GameSave.Load(file, Grid()));
			}
			File.Delete(file); GameSave.Save(file, Game().CaptureSnapshot()); Edit(file, j => j["Entities"]![0]!["Mana"] = 59); Throws<InvalidDataException>(() => GameSave.Load(file, Grid()));
			File.Copy(fixture, file, true); File.Copy(fixture, file + ".bak"); Edit(file, j => j["RandomState"] = 99);
			var recovered = GameSave.Load(file, Grid()); Check(recovered.RecoveredFromBackup && recovered.Migrated);
		});
		test("skill kill advances the same quest and blocks same-tick interaction", () =>
		{
			var world = new WorldDefinition([new("Test", Grid())], [], new(new(10), new(1), new(384, 640), "Guide"), [Monster.Id], "Clear");
			var game = new GameSimulation(1, [Player, Monster with { Health = 1 }], world: world);
			Accept(game, new(1, 1, Player.Id, Player.Region, CommandKind.Interact, Target: new(10))); game.Step(); Check(game.QuestState == QuestStage.Active);
			Accept(game, Cast(game, 2)); Accept(game, new(2, 3, Player.Id, Player.Region, CommandKind.Interact, Target: new(10))); game.Step();
			Check(game.QuestState == QuestStage.ReadyToTurnIn && game.Items.Length == 2 && game.GetEntity(Player.Id).Mana == 48);
			Check(game.Events.ToArray().Any(e => e.Kind == SimulationEventKind.InteractionFailed && e.Value == (int)InteractionFailure.Interrupted));
		});
		test("all resource fields affect the state hash and survive snapshot restoration", () =>
		{
			var state = Game().CaptureSnapshot(); string original = GameSimulation.Restore(state).ComputeStateHash();
			foreach (var actor in new[] { Player with { Mana = 59 }, Player with { MaxMana = 61 }, Player with { SkillCooldown = 1 }, Player with { Mana = 59, ManaRecoveryTicks = 1 }, Player with { SelectedSkill = SkillId.None } })
			{
				var game = GameSimulation.Restore(state with { Entities = [actor, state.Entities[1]] });
				Check(game.ComputeStateHash() != original && GameSimulation.Restore(game.CaptureSnapshot()).ComputeStateHash() == game.ComputeStateHash());
			}
		});
		test("resource upgrade preserves existing scene/save-slot identity", () =>
		{
			var scene = LegacyPlayScene.Load(PlaySceneContracts.Request(), PlayAssetContracts.Read);
			Check(scene.ContentId == "cb0c4c18a7fc408b70dec14f2526433447692c8827f5ab52c26ce1a4787b63e8");
		});
		test("warmed skill ticks allocate nothing and selection spam fits the event budget", () =>
		{
			var game = Game(Player with { HitStun = 3 });
			for (ulong i = 1; i <= 128; i++) Accept(game, Cast(game, i) with { Kind = CommandKind.SelectSkill, Target = default }); game.Step();
			Check(game.Events.Length == 129); // 128 failed selections plus monster mode transition
			for (int n = 0; n < 50; n++)
			{
				var state = Game().CaptureSnapshot(); game = GameSimulation.Restore(state); Accept(game, Cast(game));
				long before = GC.GetAllocatedBytesForCurrentThread(); game.Step(); if (n > 4) Check(GC.GetAllocatedBytesForCurrentThread() == before);
			}
		});
	}
}
