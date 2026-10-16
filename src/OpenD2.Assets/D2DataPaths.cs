namespace OpenD2.Assets;

// Where the user's genuine game data and the converted cache live. Both are outside the repo:
// the original archives are copyrighted and must never be committed or redistributed.
public static class D2DataPaths
{
	public const string DataPathVariable = "D2_DATA_PATH";
	public const string CachePathVariable = "D2_ASSET_CACHE";

	/// <summary>Explicit setting wins, then the D2_DATA_PATH environment variable. Null when neither is set.</summary>
	public static string? ResolveDataPath(string? configured = null)
	{
		string? value = string.IsNullOrWhiteSpace(configured) ? Environment.GetEnvironmentVariable(DataPathVariable) : configured;
		return string.IsNullOrWhiteSpace(value) ? null : DataDirectory.Validate(value);
	}

	/// <summary>Explicit setting, then D2_ASSET_CACHE, then a per-user cache folder. Refuses a folder inside a Git checkout.</summary>
	public static string ResolveCachePath(string? configured = null)
	{
		string? value = string.IsNullOrWhiteSpace(configured) ? Environment.GetEnvironmentVariable(CachePathVariable) : configured;
		if (string.IsNullOrWhiteSpace(value))
			value = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "OpenD2", "asset-cache");
		string full = Path.GetFullPath(value);
		RequireOutsideGit(full);
		return full;
	}

	public static void RequireOutsideGit(string path)
	{
		for (var dir = new DirectoryInfo(Path.GetFullPath(path)); dir is not null; dir = dir.Parent)
			if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git")))
				throw new InvalidOperationException("The extracted asset cache must be outside a Git checkout: " + dir.FullName);
	}
}
