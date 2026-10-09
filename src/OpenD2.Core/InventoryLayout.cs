using System.Security.Cryptography;

namespace OpenD2.Core;

public sealed record ItemFootprint(ItemDefinition Definition, string Code, int Width, int Height);

// Immutable content, independent of the asset loader. No item statistics are imported here.
public sealed class InventoryLayout
{
	public const int Width = 10, Height = 4, Cells = Width * Height;
	public static InventoryLayout Default { get; } = new([new(ItemDefinition.TrainingSword, "", 1, 3), new(ItemDefinition.TrainingVest, "", 2, 3)]);
	internal static InventoryLayout Legacy { get; } = new([new(ItemDefinition.TrainingSword, "", 1, 1), new(ItemDefinition.TrainingVest, "", 1, 1)]);
	private readonly ItemFootprint[] entries;
	public IReadOnlyList<ItemFootprint> Entries { get; }
	public string ContentHash { get; }
	public InventoryLayout(IEnumerable<ItemFootprint> definitions)
	{
		ArgumentNullException.ThrowIfNull(definitions);
		int count = Enum.GetValues<ItemDefinition>().Length;
		entries = definitions.Take(count + 1).ToArray();
		if (entries.Length != count || entries.Any(e => e is null || !Enum.IsDefined(e.Definition) || e.Code is null || e.Code.Length > 4 || e.Code.Any(c => !char.IsAsciiLetterOrDigit(c)) || e.Width is < 1 or > Width || e.Height is < 1 or > Height) || entries.Select(e => e.Definition).Distinct().Count() != count)
			throw new InvalidDataException("Inventory requires one valid footprint per preview definition, within 10×4 cells.");
		Array.Sort(entries, (a, b) => a.Definition.CompareTo(b.Definition)); Entries = Array.AsReadOnly(entries);
		using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
		writer.Write(Width); writer.Write(Height);
		foreach (var entry in entries) { writer.Write((int)entry.Definition); writer.Write(entry.Code); writer.Write(entry.Width); writer.Write(entry.Height); }
		writer.Flush(); ContentHash = Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
	}
	public ItemFootprint Get(ItemDefinition definition) => Enum.IsDefined(definition) ? entries[(int)definition] : throw new ArgumentOutOfRangeException(nameof(definition));
	public ulong Mask(ItemDefinition definition, int slot)
	{
		var size = Get(definition);
		if (slot < 0 || slot >= Cells || slot % Width + size.Width > Width || slot / Width + size.Height > Height) return 0;
		ulong row = (1UL << size.Width) - 1, mask = 0;
		for (int y = 0; y < size.Height; y++) mask |= row << (slot + y * Width);
		return mask;
	}
	public int FirstFit(ItemDefinition definition, ulong occupied)
	{
		for (int slot = 0; slot < Cells; slot++) { ulong mask = Mask(definition, slot); if (mask != 0 && (mask & occupied) == 0) return slot; }
		return -1;
	}
}
