using System.Text.Json;

namespace OpenD2.Core;

public sealed record AppSettings(int SchemaVersion = 1, string GameDataPath = "", int MaxFps = 60,
	bool Fullscreen = false, bool ShowDiagnostics = false, int MasterVolume = 80, int EffectsVolume = 80, int MusicVolume = 50, bool Muted = false, string LastScenePath = "")
{
	public void Validate()
	{
		if (SchemaVersion != 1)
			throw new InvalidDataException($"Unsupported settings version: {SchemaVersion}.");
		if (MaxFps is < 30 or > 240 || GameDataPath is null)
			throw new InvalidDataException("Settings require a data path string and FPS between 30 and 240.");
		if (LastScenePath is null || LastScenePath.Length > 4096 || LastScenePath.Any(char.IsControl))
			throw new InvalidDataException("Scene path must be a bounded string without control characters.");
		if (MasterVolume is < 0 or > 100 || EffectsVolume is < 0 or > 100 || MusicVolume is < 0 or > 100)
			throw new InvalidDataException("Audio volumes must be between 0 and 100.");
	}

	public static AppSettings Load(string path)
	{
		if (!File.Exists(path)) return new AppSettings();
		if (new FileInfo(path).Length > 65536)
			throw new InvalidDataException("Settings file exceeds 64 KiB.");
		var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path))
			?? throw new InvalidDataException("Settings file is empty.");
		settings.Validate();
		return settings;
	}

	public void Save(string path)
	{
		Validate();
		path = Path.GetFullPath(path);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
			{
				JsonSerializer.Serialize(file, this);
				file.Flush(flushToDisk: true);
			}
			if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
			File.Move(temp, path, overwrite: true);
		}
		finally
		{
			if (File.Exists(temp)) File.Delete(temp);
		}
	}
}
