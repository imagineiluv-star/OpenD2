using System.Text;
using OpenD2.Core;

// Synthetic charstats.txt, experience.txt and skills.txt in the documented lod-1.10f layout, generated from the preview numbers with
// every class starting one point stronger so a server that runs on them is visibly not running on the preview.
internal static class TableFixtures
{
    private static readonly string[] Codes = ["ama", "sor", "nec", "pal", "bar", "dru", "ass"];
    private static ClassDefinition Class(CharacterClass c, int strengthBonus) { var d = ProgressionTables.Preview.Get(c); return d with { Strength = d.Strength + strengthBonus }; }

    internal static string Charstats(int strengthBonus = 1)
    {
        var text = new StringBuilder("class\tstr\tdex\tint\tvit\tstamina\thpadd\tLifePerLevel\tStaminaPerLevel\tManaPerLevel\tLifePerVitality\tStaminaPerVitality\tManaPerMagic\tStatPerLevel\n");
        text.Append("Expansion\t\t\t\t\t\t\t\t\t\t\t\t\t\n");
        foreach (var c in Enum.GetValues<CharacterClass>())
        {
            var d = Class(c, strengthBonus);
            text.Append($"{c}\t{d.Strength}\t{d.Dexterity}\t{d.Energy}\t{d.Vitality}\t{d.BaseStamina}\t{d.BaseLife}\t{d.LifePerLevel}\t{d.StaminaPerLevel}\t{d.ManaPerLevel}\t{d.LifePerVitality}\t{d.StaminaPerVitality}\t{d.ManaPerEnergy}\t{d.StatPointsPerLevel}\n");
        }
        return text.ToString();
    }
    internal static string Experience()
    {
        var classes = Enum.GetValues<CharacterClass>();
        var text = new StringBuilder("Level\t" + string.Join('\t', classes) + "\tExpRatio\n");
        text.Append("MaxLvl\t" + string.Join('\t', classes.Select(c => ProgressionTables.Preview.MaxLevel(c))) + "\t\n");
        for (int row = 0; row < ProgressionTables.LevelCap; row++)
            text.Append(row + "\t" + string.Join('\t', classes.Select(c => ProgressionTables.Preview.ExperienceFor(c, row + 1))) + "\t1024\n");
        return text.ToString();
    }
    internal static string Skills()
    {
        var text = new StringBuilder("skill\t*Id\tcharclass\treqlevel\tmaxlvl\treqskill1\treqskill2\treqskill3\nAttack\t0\t\t1\t20\t\t\t\n");
        foreach (var s in SkillTable.Preview.All.OrderBy(s => s.Id))
        {
            var requires = s.Requires.Select(r => SkillTable.Preview.Get(r).Name).Concat(Enumerable.Repeat("", 3)).Take(3);
            text.Append($"{s.Name}\t{s.Id}\t{Codes[(int)s.Class]}\t{s.RequiredLevel}\t{s.MaxLevel}\t{string.Join('\t', requires)}\n");
        }
        return text.ToString();
    }
    // The three files flat in the folder (plain) or in the install layout with the original's upper-case names.
    internal static void Write(string folder, int strengthBonus = 1, bool installLayout = false)
    {
        string target = installLayout ? Path.Combine(folder, "data", "global", "excel") : folder;
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, installLayout ? "CharStats.TXT" : "charstats.txt"), Charstats(strengthBonus), Encoding.ASCII);
        File.WriteAllText(Path.Combine(target, installLayout ? "Experience.TXT" : "experience.txt"), Experience(), Encoding.ASCII);
        File.WriteAllText(Path.Combine(target, installLayout ? "Skills.TXT" : "skills.txt"), Skills(), Encoding.ASCII);
    }
}
