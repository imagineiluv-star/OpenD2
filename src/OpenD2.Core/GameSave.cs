using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenD2.Core;

public sealed class SaveCompatibilityException(string message) : IOException(message);
public sealed record SaveLoadResult(GameSimulation Simulation, bool RecoveredFromBackup, bool Migrated = false);

// Immutable level content is supplied by the installed content pack and pinned by hash.
// File I/O is outside Step. One writer per slot; crash remnants never become load candidates.
public static class GameSave
{
	public const int SchemaVersion = 3, MaxFileBytes = 4 * 1024 * 1024;
	private sealed record Document(int SchemaVersion, int RulesVersion, string ContentHash, string StateHash, long Tick, uint RandomState,
		EntityState[] Entities, CommandCursor[] Inputs, GameCommand[] PendingCommands, EntityId WorldPlayer, QuestStage QuestState, ItemState[] Items, ItemFootprint[]? Inventory = null);
	private static readonly JsonSerializerOptions Options = new()
	{
		MaxDepth = 32, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true
	};
	private static string ContentHash(CollisionGrid? grid, WorldDefinition? world)
	{
		if (world is not null) return world.ContentHash;
		if (grid is null) return "free-movement-v1";
		using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
		writer.Write(grid.Region.Value); writer.Write(grid.Origin.X); writer.Write(grid.Origin.Y); writer.Write(grid.Width); writer.Write(grid.Height);
		foreach (var cell in grid.Cells) writer.Write((byte)cell);
		writer.Flush(); return Convert.ToHexStringLower(SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length)));
	}
	private static byte[] ReadBytes(string path)
	{
		using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
		if (file.Length is < 1 or > MaxFileBytes) throw new InvalidDataException("Save exceeds file budget or is empty.");
		var data = new byte[(int)file.Length]; file.ReadExactly(data); return data;
	}
	private static SaveLoadResult Decode(byte[] data, CollisionGrid? collision, WorldDefinition? world, InventoryLayout inventory)
	{
		try
		{
			// Inspect compatibility before full deserialization so future files are never
			// misclassified as corrupt merely because they contain new fields.
			using var header = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 32 });
			var root = header.RootElement;
			if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("SchemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int version) ||
				!root.TryGetProperty("RulesVersion", out var rules) || rules.ValueKind != JsonValueKind.Number || !rules.TryGetInt32(out int rulesVersion)) throw new InvalidDataException("Save header missing.");
			bool oldGrid = version == 1 && rulesVersion == 4;
			bool legacy = oldGrid || (version == 2 && rulesVersion == 5);
			if (!legacy && (version != SchemaVersion || rulesVersion != GameSimulation.RulesVersion)) throw new SaveCompatibilityException("Unsupported save schema or game rules. File preserved.");
			if (!root.TryGetProperty("ContentHash", out var content) || content.ValueKind != JsonValueKind.String) throw new InvalidDataException("Missing content identity.");
			if (content.GetString() != ContentHash(collision, world)) throw new SaveCompatibilityException("Save requires different level content. File preserved.");
			// Resource fields are mandatory in v3 and forbidden in old checksummed formats.
			if (!root.TryGetProperty("Entities", out var actors) || actors.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Missing entities.");
			foreach (var actor in actors.EnumerateArray())
			{
				if (actor.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid entity.");
				foreach (string field in new[] { "Mana", "MaxMana", "SkillCooldown", "ManaRecoveryTicks", "SelectedSkill" })
					if (actor.TryGetProperty(field, out _) == legacy) throw new InvalidDataException("Resource fields disagree with save version.");
			}
			var doc = JsonSerializer.Deserialize<Document>(data, Options) ?? throw new InvalidDataException("Empty save.");
			if (oldGrid ? doc.Inventory is not null : doc.Inventory is null) throw new InvalidDataException("Invalid inventory layout for this save version.");
			var layout = oldGrid ? InventoryLayout.Legacy : new InventoryLayout(doc.Inventory!);
			if (!oldGrid && layout.ContentHash != inventory.ContentHash) throw new SaveCompatibilityException("Save requires different item dimensions/codes. File preserved.");
			var state = new SimulationSnapshot(doc.RulesVersion, doc.Tick, doc.RandomState, doc.Entities, doc.Inputs, doc.PendingCommands,
				collision, world, doc.WorldPlayer, doc.QuestState, doc.Items, layout);
			var game = legacy ? GameSimulation.RestoreLegacy(state) : GameSimulation.Restore(state);
			if (doc.StateHash != game.ComputeStateHash(doc.RulesVersion)) throw new InvalidDataException("Save state checksum mismatch.");
			if (oldGrid) game = Migrate(game, inventory);
			if (legacy)
			{
				var upgraded = game.CaptureSnapshot();
				game = GameSimulation.Restore(upgraded with { Entities = upgraded.Entities.Select(e => e with
					{ Mana = 60, MaxMana = 60, SkillCooldown = 0, ManaRecoveryTicks = 0, SelectedSkill = SkillId.PowerStrike }).ToArray() });
			}
			return new(game, false, legacy);
		}
		catch (JsonException error) { throw new InvalidDataException("Malformed save JSON.", error); }
	}
	private static GameSimulation Migrate(GameSimulation legacy, InventoryLayout layout)
	{
		var state = legacy.CaptureSnapshot(); var items = state.Items.ToArray(); var occupied = new Dictionary<EntityId, ulong>();
		// Stable old-slot order, independent of drop order. No repacking of an already migrated save.
		foreach (int index in Enumerable.Range(0, items.Length).Where(i => items[i].Location == ItemLocation.Inventory).OrderBy(i => items[i].Owner.Value).ThenBy(i => items[i].Slot))
		{
			var item = items[index]; ulong mask = occupied.GetValueOrDefault(item.Owner); int slot = layout.FirstFit(item.Definition, mask);
			if (slot < 0) throw new SaveCompatibilityException("The old bag does not fit the 10×4 grid. Open it with the previous app, reduce carried items, then retry. Files preserved.");
			items[index] = item with { Slot = slot }; occupied[item.Owner] = mask | layout.Mask(item.Definition, slot);
		}
		return GameSimulation.Restore(state with { RulesVersion = GameSimulation.RulesVersion, Inventory = layout, Items = items });
	}
	public static SaveLoadResult Load(string path, CollisionGrid? collision = null, WorldDefinition? world = null, InventoryLayout? inventory = null)
	{
		if (collision is not null && world is not null) throw new ArgumentException("Select one content source.");
		inventory ??= InventoryLayout.Default;
		try { return Decode(ReadBytes(path), collision, world, inventory); }
		catch (Exception primary) when (primary is InvalidDataException or FileNotFoundException)
		{
			try { return Decode(ReadBytes(path + ".bak"), collision, world, inventory) with { RecoveredFromBackup = true }; }
			catch (Exception backup) when (backup is InvalidDataException or FileNotFoundException)
			{ throw new InvalidDataException("Neither save nor backup could be loaded. Files preserved.", new AggregateException(primary, backup)); }
		}
	}
	public static void Save(string path, SimulationSnapshot checkpoint)
	{
		var game = GameSimulation.Restore(checkpoint); // validate before touching existing files
		var doc = new Document(SchemaVersion, GameSimulation.RulesVersion, ContentHash(game.Collision, game.World), game.ComputeStateHash(), game.Tick, game.RandomState,
			game.Entities.ToArray(), checkpoint.Inputs.ToArray(), checkpoint.PendingCommands.ToArray(), game.WorldPlayer, game.QuestState, game.Items.ToArray(), game.Inventory.Entries.ToArray());
		byte[] data = JsonSerializer.SerializeToUtf8Bytes(doc, Options);
		if (data.Length > MaxFileBytes) throw new InvalidDataException("Save exceeds file budget.");
		path = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		// Keep the lock file inode stable; the OS releases its lock when the writer exits.
		using var slotLock = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
		byte[]? previous = null;
		try
		{
			previous = ReadBytes(path); Decode(previous, checkpoint.World is null ? checkpoint.Collision : null, checkpoint.World, game.Inventory);
		}
		catch (Exception error) when (error is FileNotFoundException or InvalidDataException) { previous = null; }
		if (previous is null && File.Exists(path + ".bak"))
		{
			try { Decode(ReadBytes(path + ".bak"), checkpoint.World is null ? checkpoint.Collision : null, checkpoint.World, game.Inventory); }
			catch (InvalidDataException) { } // preserve corrupt backup; compatibility errors still stop the write
		}
		if (previous is not null) AtomicWrite(path + ".bak", previous);
		AtomicWrite(path, data);
	}
	private static void AtomicWrite(string path, byte[] data)
	{
		string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
			{ file.Write(data); file.Flush(flushToDisk: true); }
			File.Move(temporary, path, overwrite: true);
		}
		finally { if (File.Exists(temporary)) File.Delete(temporary); }
	}
}
