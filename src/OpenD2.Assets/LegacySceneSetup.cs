using System.Text.Json;
using OpenD2.Core;

namespace OpenD2.Assets;

// A single-region preview with user-selected positions; never an original campaign importer.
public static class LegacySceneSetup
{
	public static LegacySceneRequest Create(LegacyMapRequest map, string title,
		int playerX, int playerY, int monsterX, int monsterY, int npcX, int npcY)
	{
		ArgumentNullException.ThrowIfNull(map); map.Validate();
		if (string.IsNullOrWhiteSpace(title) || title.Length > 80 || title.Any(char.IsControl))
			throw new InvalidDataException("Enter a scene title of 1..80 characters.");
		int Center(int cell)
		{
			if (cell is < 0 or > 4095) throw new InvalidDataException("Navigation cell coordinates must be 0..4095.");
			return cell * CollisionGrid.CellSize + CollisionGrid.CellSize / 2;
		}
		var positions = new[] { (Center(playerX), Center(playerY)), (Center(monsterX), Center(monsterY)), (Center(npcX), Center(npcY)) };
		if (positions.Distinct().Count() != 3) throw new InvalidDataException("Player, monster and guide must use different cells.");
		return new(1, title, [new(1, title, map with { Tilesets = map.Tilesets.ToArray() })],
			[new(1, 1, positions[0].Item1, positions[0].Item2, true), new(2, 1, positions[1].Item1, positions[1].Item2, false, 36)],
			new(10, 1, positions[2].Item1, positions[2].Item2, "Preview guide"), [], [2], NavigateWalls: true);
	}

	// Validate resources and connectivity before creating any file. Existing files are never replaced.
	public static PlaySceneReadiness SaveNew(string file, LegacySceneRequest request, Func<string, byte[]> read)
	{
		byte[] json = JsonSerializer.SerializeToUtf8Bytes(request, new JsonSerializerOptions { WriteIndented = true });
		if (json.Length > 262144) throw new InvalidDataException("Scene request exceeds 256 KiB.");
		var snapshot = JsonSerializer.Deserialize<LegacySceneRequest>(json) ?? throw new InvalidDataException("Scene request is empty.");
		var readiness = PlaySceneReadiness.Check(LegacyPlayScene.Load(snapshot, read));
		if (!readiness.QuestLoopReachable) throw new InvalidDataException("Choose connected cells for the player, guide and monster: " + string.Join(", ", readiness.Issues));
		file = Path.GetFullPath(file);
		Directory.CreateDirectory(Path.GetDirectoryName(file)!);
		string temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
			{ output.Write(json); output.Flush(flushToDisk: true); }
			File.Move(temporary, file, overwrite: false);
		}
		finally { if (File.Exists(temporary)) File.Delete(temporary); }
		return readiness;
	}
}
