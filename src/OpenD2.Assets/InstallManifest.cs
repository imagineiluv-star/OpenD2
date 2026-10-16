using System.Security.Cryptography;
using System.Text.Json;

namespace OpenD2.Assets;

public sealed record ManifestFile(string Sha256, long Size);
public sealed record ManifestVersion(string Id, string Description, bool Placeholder, IReadOnlyDictionary<string, ManifestFile> Files);
public sealed record ArchiveHash(string Name, long Size, string Sha256);
/// <summary>Version is null when no non-placeholder manifest entry matches every archive found.</summary>
public sealed record InstallVerification(string Directory, string? Version, IReadOnlyList<ArchiveHash> Archives,
	IReadOnlyList<string> Missing, IReadOnlyList<string> Mismatched, IReadOnlyList<string> Unknown)
{
	public bool Verified => Version is not null;
}

// Per-version SHA-256 manifest of the user's MPQs. Hashes are not game data and may be committed.
public sealed class InstallManifest
{
	public IReadOnlyList<ManifestVersion> Versions { get; }
	private InstallManifest(IReadOnlyList<ManifestVersion> versions) { Versions = versions; }

	public static InstallManifest Default { get; } = LoadDefault();
	private static InstallManifest LoadDefault()
	{
		using var stream = typeof(InstallManifest).Assembly.GetManifestResourceStream("OpenD2.Assets.d2-mpq-manifest.json")
			?? throw new InvalidOperationException("Embedded MPQ manifest is missing.");
		using var reader = new StreamReader(stream);
		return Parse(reader.ReadToEnd());
	}

	public static InstallManifest Parse(string json)
	{
		using var document = JsonDocument.Parse(json);
		var root = document.RootElement;
		if (root.GetProperty("schema").GetInt32() != 1) throw new InvalidDataException("Unsupported MPQ manifest schema.");
		var versions = new List<ManifestVersion>();
		foreach (var v in root.GetProperty("versions").EnumerateArray())
		{
			var files = new Dictionary<string, ManifestFile>(StringComparer.OrdinalIgnoreCase);
			foreach (var f in v.GetProperty("files").EnumerateObject())
			{
				string hash = f.Value.GetProperty("sha256").GetString()!;
				if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid SHA-256 for " + f.Name);
				files.Add(f.Name, new(hash.ToLowerInvariant(), f.Value.GetProperty("size").GetInt64()));
			}
			bool placeholder = v.GetProperty("placeholder").GetBoolean();
			if (!placeholder && files.Count == 0) throw new InvalidDataException("Version without files must be marked placeholder.");
			versions.Add(new(v.GetProperty("id").GetString()!, v.GetProperty("description").GetString() ?? "", placeholder, files));
		}
		return new(versions);
	}

	/// <summary>Hashes every *.mpq in the directory and finds the version whose manifest matches.</summary>
	/// <param name="hashCache">Optional JSON file that remembers hashes by name, size and mtime to skip re-reading gigabytes.</param>
	/// <param name="rehash">Ignore remembered hashes and re-read every archive (full SHA-256).</param>
	public InstallVerification Verify(string directory, string? hashCache = null, string? expectedVersion = null, bool rehash = false)
	{
		directory = DataDirectory.Validate(directory);
		var remembered = rehash ? new Dictionary<string, string>(StringComparer.Ordinal) : LoadHashCache(hashCache);
		var archives = new List<ArchiveHash>();
		foreach (var path in Directory.EnumerateFiles(directory).Where(p => Path.GetExtension(p).Equals(".mpq", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
		{
			var info = new FileInfo(path); string name = info.Name.ToLowerInvariant();
			string key = $"{name}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
			if (!remembered.TryGetValue(key, out var hash))
			{
				using var stream = File.OpenRead(path);
				hash = Convert.ToHexStringLower(SHA256.HashData(stream));
				remembered[key] = hash;
			}
			archives.Add(new(name, info.Length, hash));
		}
		SaveHashCache(hashCache, remembered);
		var found = archives.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);
		ManifestVersion? best = null; string[] missing = [], mismatched = [];
		foreach (var version in Versions.Where(v => !v.Placeholder && (expectedVersion is null || v.Id == expectedVersion)))
		{
			var miss = version.Files.Keys.Where(n => !found.ContainsKey(n)).Order(StringComparer.Ordinal).ToArray();
			var bad = version.Files.Where(f => found.TryGetValue(f.Key, out var a) && (a.Sha256 != f.Value.Sha256 || a.Size != f.Value.Size)).Select(f => f.Key).Order(StringComparer.Ordinal).ToArray();
			if (miss.Length == 0 && bad.Length == 0) { best = version; missing = []; mismatched = []; break; }
			if (best is null && (missing.Length + mismatched.Length == 0 || miss.Length + bad.Length < missing.Length + mismatched.Length)) { missing = miss; mismatched = bad; }
		}
		var known = best?.Files.Keys ?? Enumerable.Empty<string>();
		var unknown = archives.Select(a => a.Name).Except(known, StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
		return new(directory, best?.Id, archives, missing, mismatched, best is null ? [] : unknown);
	}

	private static Dictionary<string, string> LoadHashCache(string? path)
	{
		if (path is null || !File.Exists(path)) return new(StringComparer.Ordinal);
		try { return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new(StringComparer.Ordinal); }
		catch (JsonException) { return new(StringComparer.Ordinal); }
	}
	private static void SaveHashCache(string? path, Dictionary<string, string> hashes)
	{
		if (path is null) return;
		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
		File.WriteAllText(path, JsonSerializer.Serialize(hashes));
	}
}
