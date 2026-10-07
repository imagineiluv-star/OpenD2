namespace OpenD2.Assets;

// M0 checks the directory only. MPQ/version validation is M1, not implied here.
public static class DataDirectory
{
	public static string Validate(string path)
	{
		if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Select an existing game data directory.", nameof(path));
		var full = Path.GetFullPath(path);
		if (!Directory.Exists(full)) throw new DirectoryNotFoundException("Game data directory does not exist.");
		using var entries = Directory.EnumerateFileSystemEntries(full).GetEnumerator();
		_ = entries.MoveNext(); // Check read access without traversing/extracting user data.
		return full;
	}
}
