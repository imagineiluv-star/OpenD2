namespace OpenD2.Core;

public sealed record AppPaths(string Root)
{
	public string SettingsFile => Path.Combine(Root, "settings.json");
	public string Saves => Path.Combine(Root, "saves");
	public string Cache => Path.Combine(Root, "cache");
	public string Logs => Path.Combine(Root, "logs");

	public static AppPaths ForCurrentUser()
	{
		var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		if (string.IsNullOrWhiteSpace(root))
			throw new IOException("The operating system did not provide a user data directory.");
		return new AppPaths(Path.Combine(root, "OpenD2"));
	}

	public void EnsureCreated()
	{
		foreach (var path in new[] { Root, Saves, Cache, Logs })
			Directory.CreateDirectory(path);
	}
}
