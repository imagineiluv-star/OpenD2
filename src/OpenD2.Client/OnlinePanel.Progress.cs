using Godot;
using OpenD2.Core;
using OpenD2.Online;

namespace OpenD2.Client;

// Class choice, progress readout and point spending for the selected character. The rules and numbers belong to the server
// (progress arrives on the character, the skill table from /v1/skills); this panel only shows them and sends the player's choices.
public partial class OnlinePanel
{
	private readonly OptionButton characterClass = new();
	private readonly OptionButton skillChoice = new();
	private readonly Label progressText = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart };
	private SkillInfo[] skillList = [];
	private MonsterInfo[] monsterList = [];

	// Index 0 is "no class": the character keeps the fixed pool and earns no experience.
	private CharacterClass? SelectedClass => characterClass.Selected <= 0 ? null : (CharacterClass)(characterClass.Selected - 1);

	private static string CharacterLabel(CharacterInfo c) =>
		c.Progress is { } p ? $"{c.Name} — {p.Class} Lv {p.Level} ({c.Victories} victories)" : $"{c.Name} ({c.Victories} victories)";

	private void AddProgressControls()
	{
		characterClass.AddItem("No class (legacy)");
		foreach (var cls in Enum.GetValues<CharacterClass>()) characterClass.AddItem(cls.ToString());
		characterClass.TooltipText = "Class of the next character you create. A character without a class keeps the fixed pool and earns no experience.";
		characters.ItemSelected += _ => ShowProgress();
		AddChild(progressText);
		var spend = new HFlowContainer(); AddChild(spend);
		foreach (var stat in Enum.GetValues<CharacterStat>())
			Button(spend, "+ " + stat, async () =>
			{
				RequireLogin(); var updated = await client!.SpendStat(SelectedCharacter(), stat, lifetime.Token);
				status.Text = $"{updated.Name}: +1 {stat}."; await Refresh();
			});
		spend.AddChild(skillChoice);
		Button(spend, "Add skill point", async () =>
		{
			RequireLogin(); if (skillChoice.Selected < 0) throw new InvalidOperationException("Select a skill.");
			int skill = (int)skillChoice.GetItemMetadata(skillChoice.Selected).AsInt64();
			var updated = await client!.SpendSkill(SelectedCharacter(), skill, lifetime.Token);
			status.Text = $"{updated.Name}: +1 {skillList.First(s => s.Id == skill).Name}."; await Refresh();
		});
		ShowProgress();
	}

	private void ShowProgress()
	{
		long chosen = skillChoice.Selected >= 0 ? skillChoice.GetItemMetadata(skillChoice.Selected).AsInt64() : -1;
		skillChoice.Clear();
		var selected = characters.Selected >= 0 && characters.Selected < characterList.Length ? characterList[characters.Selected] : null;
		if (selected is null) { progressText.Text = ""; skillChoice.Disabled = true; return; }
		if (selected.Progress is not { } p)
		{
			progressText.Text = "No class: fixed pool (100 life / 60 mana); earns no experience.";
			skillChoice.Disabled = true; return;
		}
		var ranks = p.Skills.Select(r => $"{SkillName(r.Skill)} {r.Level}");
		progressText.Text = $"{p.Class} · level {p.Level} · experience {p.Experience:N0} (room experience is added when the host closes the room)\n" +
			$"To spend: {p.StatPoints} attribute, {p.SkillPoints} skill · Strength {p.Strength} · Dexterity {p.Dexterity} · Vitality {p.Vitality} · Energy {p.Energy}\n" +
			"Skills: " + (p.Skills.Length == 0 ? "none yet" : string.Join(", ", ranks)) + "\nPoints can be spent between rooms only.";
		foreach (var skill in skillList.Where(s => s.Class == p.Class.ToString()))
		{
			skillChoice.AddItem($"{skill.Name} ({p.SkillLevel(skill.Id)}/{skill.MaxLevel}, needs level {skill.RequiredLevel})");
			skillChoice.SetItemMetadata(skillChoice.ItemCount - 1, skill.Id);
			if (skill.Id == chosen) skillChoice.Select(skillChoice.ItemCount - 1);
		}
		skillChoice.Disabled = skillChoice.ItemCount == 0;
	}

	private string SkillName(int id) => skillList.FirstOrDefault(s => s.Id == id)?.Name ?? "Skill " + id;

	// Headless check (packaged server): the panel creates a class character, lists the server's skills and shows the progress.
	private async Task CheckProgressUi(string role)
	{
		if (!client!.SupportsCharacterProgress) throw new InvalidDataException("Packaged server did not advertise character progression.");
		if (!client.SupportsMonsterKinds) throw new InvalidDataException("Packaged server did not advertise monster kinds.");
		var monsters = await client.ListMonsters(lifetime.Token);
		if (monsters.Length == 0 || Arena.Roster.Kinds.Values.Concat(CooperativeWorld.Roster.Kinds.Values).Any(code => !monsters.Any(m => m.Code == code)))
			throw new InvalidDataException("Server monster table does not cover the built-in rooms.");
		var skills = await client.ListSkills(lifetime.Token);
		if (skills.Length == 0 || skills.Any(s => s.Name.Length == 0 || !Enum.TryParse<CharacterClass>(s.Class, out _))) throw new InvalidDataException("Server skill table is not usable.");
		characterClass.Select(1 + (int)CharacterClass.Sorceress);
		if (SelectedClass != CharacterClass.Sorceress) throw new InvalidDataException("Class selection binding failed.");
		var made = await client.CreateCharacter(role + "Mage", SelectedClass, lifetime.Token);
		characterClass.Select(0);
		await Refresh();
		int index = Array.FindIndex(characterList, c => c.Id == made.Id);
		if (index < 0) throw new InvalidDataException("New class character is missing from the list.");
		characters.Select(index); ShowProgress();
		if (!characters.GetItemText(index).Contains("Sorceress Lv 1", StringComparison.Ordinal) || !progressText.Text.Contains("Sorceress · level 1", StringComparison.Ordinal) ||
			skillChoice.Disabled || skillChoice.ItemCount != skills.Count(s => s.Class == "Sorceress"))
			throw new InvalidDataException("Progress UI binding failed.");
		await client.Send<object>(System.Net.Http.HttpMethod.Delete, "v1/characters/" + made.Id, cancellation: lifetime.Token);
		await Refresh();
		GD.Print("OPEND2_PROGRESS_UI_PASS");
	}
}
