using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using OpenD2.Core;

namespace OpenD2.Online;

// A portable Open profile, not a room snapshot or a trusted Realm character.
public sealed record OpenCharacter(int Version, int Rules, string Mode, string Name, int Victories);

public static class OpenCharacterFile
{
    public const int MaximumBytes = 8192;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 8
    };
    public static void Validate(OpenCharacter? value)
    {
        if (value is null || value.Version != 1 || value.Rules != GameSimulation.RulesVersion || value.Mode != "open" ||
            value.Name is null || !Regex.IsMatch(value.Name, "\\A[a-zA-Z0-9_-]{3,24}\\z") || value.Victories is < 0 or > 1_000_000)
            throw new InvalidDataException("Invalid Open character profile, version or game rules.");
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
            Validate(value); return value!;
        }
        catch (JsonException error) { throw new InvalidDataException("Invalid Open character JSON.", error); }
    }
    public static void Save(string path, OpenCharacter value)
    {
        Validate(value);
        string destination = Path.GetFullPath(path), temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var output = new FileStream(temporary, options))
            { output.Write(JsonSerializer.SerializeToUtf8Bytes(value, Json)); output.Flush(true); }
            // Snapshots never replace an existing save, including during concurrent exports.
            File.Move(temporary, destination, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
