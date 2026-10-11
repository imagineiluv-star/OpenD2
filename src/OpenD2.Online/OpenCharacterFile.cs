using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using OpenD2.Core;

namespace OpenD2.Online;

// A portable Open profile, not a room snapshot or a trusted Realm character.
public sealed record OpenItem(ItemDefinition Definition, ItemLocation Location, int Slot);
public sealed record OpenCharacter(int Version, int Rules, string Mode, string Name, int Victories, OpenItem[]? Inventory = null);

public static class OpenCharacterFile
{
    public const int MaximumBytes = 8192;
    public const int MaximumItems = InventoryLayout.Cells + 2 + GameSimulation.BeltCapacity;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
        MaxDepth = 8
    };
    public static void Validate(OpenCharacter? value)
    {
        if (value is null || value.Version is not (1 or 2) || value.Mode != "open" ||
            value.Name is null || !Regex.IsMatch(value.Name, "\\A[a-zA-Z0-9_-]{3,24}\\z") || value.Victories is < 0 or > 1_000_000 ||
            (value.Version == 1 && (value.Rules is not (7 or GameSimulation.RulesVersion) || value.Inventory is not null)) ||
            (value.Version == 2 && (value.Rules != GameSimulation.RulesVersion || value.Inventory is null)))
            throw new InvalidDataException("Invalid Open character file, version or game rules.");
        if (value.Inventory is { } items)
        {
            if (items.Length > MaximumItems || items.Any(i => i is null))
                throw new InvalidDataException("Open inventory exceeds its item budget or contains null items.");
            // Reuse Core's definition, placement, overlap and equipment/belt rules.
            // No source-room item IDs, player IDs or positions cross the file boundary.
            Arena.Create(1).WithCarriedItems(items.Select(i => new CarriedItem(new(1), i.Definition, i.Location, i.Slot)).ToArray());
        }
    }
    public static OpenCharacter Upgrade(OpenCharacter value)
    {
        Validate(value);
        return value with { Version = 2, Rules = GameSimulation.RulesVersion, Inventory = value.Inventory?.ToArray() ?? [] };
    }
    public static OpenCharacter Load(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > MaximumBytes) throw new InvalidDataException("Open character exceeds 8 KiB.");
        byte[] bytes = new byte[MaximumBytes + 1];
        int length = file.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        if (length > MaximumBytes) throw new InvalidDataException("Open character exceeds 8 KiB.");
        try
        {
            var value = JsonSerializer.Deserialize<OpenCharacter>(bytes.AsSpan(0, length), Json);
            return Upgrade(value!);
        }
        catch (JsonException error) { throw new InvalidDataException("Invalid Open character JSON.", error); }
    }
    public static void Save(string path, OpenCharacter value)
    {
        value = Upgrade(value);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Open character exceeds 8 KiB.");
        string destination = Path.GetFullPath(path), temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var output = new FileStream(temporary, options))
            { output.Write(bytes); output.Flush(true); }
            // Snapshots never replace an existing save, including during concurrent exports.
            File.Move(temporary, destination, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
