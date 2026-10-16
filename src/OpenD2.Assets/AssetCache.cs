using System.Text;
using System.Text.Json;

namespace OpenD2.Assets;

/// <summary>A readable set of logical D2 resources: the user's MPQs, or the converted local cache.</summary>
public interface IAssetSource : IDisposable
{
	IReadOnlyList<string> List();
	bool TryRead(string logicalPath, long maxBytes, out byte[] data);
}

// Reads the installation's MPQs in priority order, keeping archives open for bulk extraction.
public sealed class MpqAssetSource : IAssetSource
{
	private readonly List<MpqArchive> archives = [];
	public MpqAssetSource(string directory)
	{
		try { foreach (var path in GameInstall.Probe(directory).Archives) archives.Add(new MpqArchive(path)); }
		catch { Dispose(); throw; }
	}
	public IReadOnlyList<string> List() => archives.SelectMany(a => a.ListNames(1000000)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
	public bool TryRead(string logicalPath, long maxBytes, out byte[] data)
	{
		logicalPath = MpqArchive.NormalizePath(logicalPath);
		foreach (var archive in archives)
		{
			try { data = archive.Read(logicalPath, maxBytes); return true; }
			catch (MpqException error) when (error.Code == 2) { }
		}
		data = []; return false;
	}
	public void Dispose() { foreach (var a in archives) a.Dispose(); archives.Clear(); }
}

// Reads raw resources previously extracted by AssetCache.Build.
public sealed class CacheAssetSource(string cacheDirectory) : IAssetSource
{
	private readonly string root = Path.Combine(Path.GetFullPath(cacheDirectory), "raw");
	public IReadOnlyList<string> List() => Directory.Exists(root)
		? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(root, p).Replace(Path.DirectorySeparatorChar, '\\')).Order(StringComparer.Ordinal).ToArray()
		: [];
	public bool TryRead(string logicalPath, long maxBytes, out byte[] data)
	{
		string path = AssetCache.RawPath(Path.GetDirectoryName(root)!, logicalPath);
		if (!File.Exists(path)) { data = []; return false; }
		if (new FileInfo(path).Length > maxBytes) throw new InvalidDataException("Cached resource exceeds read limit.");
		data = File.ReadAllBytes(path); return true;
	}
	public void Dispose() { }
}

public sealed record AssetCacheOptions
{
	/// <summary>Kinds copied raw into the cache (readable by the engine). Audio is opt-in because of its size.</summary>
	public IReadOnlySet<string> RawKinds { get; init; } = new HashSet<string> { "palette", "excel_txt", "text_tbl", "dc6", "dcc", "cof", "dt1", "ds1" };
	/// <summary>Image kinds converted to PNG. DCC is opt-in: decoding every direction of every DCC is slow and large.</summary>
	public IReadOnlySet<string> PngKinds { get; init; } = new HashSet<string> { "dc6" };
	public string PngPalette { get; init; } = "act1";
	public Func<string, bool>? Include { get; init; }
	/// <summary>Total PNG output caps; once either is reached no further PNGs are written and the result reports PngCapped.</summary>
	public int MaxPngFiles { get; init; } = 250_000;
	public long MaxPngBytes { get; init; } = 4L * 1024 * 1024 * 1024;
	public string Signature => $"{string.Join(',', RawKinds.Order(StringComparer.Ordinal))}|{string.Join(',', PngKinds.Order(StringComparer.Ordinal))}|{PngPalette}|{MaxPngFiles}|{MaxPngBytes}";
}

public sealed record AssetCacheResult(bool Rebuilt, int Resources, int PngFiles, int Tables, int Palettes, IReadOnlyList<string> Errors)
{
	public bool PngCapped { get; init; }
	public long PngBytes { get; init; }
}

// Converts user-supplied MPQ content into a local cache OUTSIDE the repository. Rebuilds only when the
// source fingerprint (the archives' SHA-256), converter version or options change.
public static class AssetCache
{
	public const string ConverterVersion = "p1-mpq-3";
	public const string ManifestName = "cache-manifest.json";
	private const int MaxErrors = 200;

	public static bool IsCache(string directory) => File.Exists(Path.Combine(directory, ManifestName));

	/// <summary>True only for a manifest this converter wrote (has a "converter" starting with "p1-mpq").</summary>
	public static bool IsOwnedCache(string directory)
	{
		string path = Path.Combine(directory, ManifestName);
		if (!File.Exists(path)) return false;
		try
		{
			using var doc = JsonDocument.Parse(File.ReadAllText(path));
			return doc.RootElement.TryGetProperty("converter", out var c) && c.GetString()?.StartsWith("p1-mpq", StringComparison.Ordinal) == true;
		}
		catch (JsonException) { return false; }
	}

	// Windows reserves these names with any extension; refuse them so a cache stays portable and safe.
	private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
	{ "con", "prn", "aux", "nul", "conin$", "conout$",
	  "com0", "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9", "com\u00b9", "com\u00b2", "com\u00b3",
	  "lpt0", "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9", "lpt\u00b9", "lpt\u00b2", "lpt\u00b3" };
	public static bool IsDeviceName(string segment) => DeviceNames.Contains(segment.Split('.')[0].TrimEnd(' '));
	/// <summary>Device names, and names Windows silently rewrites (trailing dot or space), are not safe cache segments.</summary>
	public static bool IsUnsafeSegment(string segment) => segment.Length == 0 || segment.EndsWith('.') || segment.EndsWith(' ') || IsDeviceName(segment);
	private static string[] SafeSegments(string logicalPath)
	{
		var parts = MpqArchive.NormalizePath(logicalPath).Split('\\');
		if (parts.Any(IsUnsafeSegment)) throw new ArgumentException("Path contains a reserved Windows device name or a trailing dot/space: " + logicalPath);
		return parts;
	}

	public static string RawPath(string cacheDirectory, string logicalPath) =>
		Path.Combine([Path.GetFullPath(cacheDirectory), "raw", .. SafeSegments(logicalPath)]);

	public static string Fingerprint(IEnumerable<ArchiveHash> archives) =>
		Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(
			string.Join('\n', archives.OrderBy(a => a.Name, StringComparer.Ordinal).Select(a => $"{a.Name}:{a.Size}:{a.Sha256}")))));

	public static bool IsCurrent(string cacheDirectory, string sourceFingerprint, AssetCacheOptions options)
	{
		string path = Path.Combine(cacheDirectory, ManifestName);
		if (!File.Exists(path)) return false;
		try
		{
			using var doc = JsonDocument.Parse(File.ReadAllText(path));
			var r = doc.RootElement;
			return r.GetProperty("converter").GetString() == ConverterVersion && r.GetProperty("source").GetString() == sourceFingerprint
				&& r.GetProperty("options").GetString() == options.Signature
				// A cache that hit the PNG cap is incomplete: never treat it as current, so the next build retries.
				&& !(r.TryGetProperty("pngCapped", out var capped) && capped.ValueKind == JsonValueKind.True);
		}
		catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { return false; }
	}

	public static AssetCacheResult Build(IAssetSource source, string sourceFingerprint, string cacheDirectory, AssetCacheOptions? options = null)
	{
		options ??= new();
		cacheDirectory = Path.GetFullPath(cacheDirectory);
		D2DataPaths.RequireOutsideGit(cacheDirectory);
		if (Path.GetFileName(cacheDirectory).Length == 0 || Path.GetPathRoot(cacheDirectory) == cacheDirectory) throw new ArgumentException("Refusing to use a filesystem root as the asset cache.");
		if (Path.GetFileName(cacheDirectory).Split(Path.DirectorySeparatorChar).Any(IsUnsafeSegment)) throw new ArgumentException("Cache folder uses a reserved Windows device name or a trailing dot/space.");
		RequireReplaceable(cacheDirectory);
		Directory.CreateDirectory(Path.GetDirectoryName(cacheDirectory)!);
		// One builder per cache folder; a second concurrent build fails fast instead of racing the swap.
		using var gate = AcquireLock(cacheDirectory + ".lock");
		if (IsCurrent(cacheDirectory, sourceFingerprint, options)) return new(false, 0, 0, 0, 0, []);
		string staging = cacheDirectory + ".building-" + Guid.NewGuid().ToString("N")[..8];
		Directory.CreateDirectory(staging);
		var errors = new List<string>(); int resources = 0, pngs = 0, tables = 0, palettes = 0; long pngBytes = 0; bool capped = false;
		try
		{
			Palette? pngPalette = null;
			if (options.PngKinds.Count > 0 && source.TryRead($"data\\global\\palette\\{options.PngPalette}\\pal.dat", 768, out var pal)) pngPalette = Palette.Parse(pal);
			bool PngBudget()
			{
				if (pngs < options.MaxPngFiles && pngBytes < options.MaxPngBytes) return true;
				if (!capped) { capped = true; errors.Add($"PNG output cap reached ({pngs} files, {pngBytes} bytes); remaining PNGs skipped"); }
				return false;
			}
			// The byte cap is checked before writing, so the output never exceeds MaxPngBytes.
			bool WritePng(string path, byte[] png)
			{
				if (pngBytes + png.Length > options.MaxPngBytes)
				{
					if (!capped) { capped = true; errors.Add($"PNG output cap reached ({pngs} files, {pngBytes} bytes); remaining PNGs skipped"); }
					return false;
				}
				Write(path, png); pngs++; pngBytes += png.Length; return true;
			}
			foreach (string name in source.List())
			{
				string? kind = AssetDecoders.Kind(name);
				if (kind is null || options.Include?.Invoke(name) == false) continue;
				bool raw = options.RawKinds.Contains(kind), png = options.PngKinds.Contains(kind);
				if (!raw && !png && kind is not "palette" and not "excel_txt") continue;
				try
				{
					if (!source.TryRead(name, AssetDecoders.MaxInputBytes, out var data)) continue;
					if (raw) { Write(RawPath(staging, name), data); resources++; }
					switch (kind)
					{
						case "palette":
							var colors = Palette.Parse(data);
							string act = name.Split('\\')[^2];
							if (IsUnsafeSegment(act)) throw new ArgumentException("Reserved device name or trailing dot/space: " + name);
							var swatch = new byte[256]; for (int i = 0; i < 256; i++) swatch[i] = (byte)i;
							Write(Path.Combine(staging, "palettes", act + ".png"), PngWriter.EncodeRgba(16, 16, colors.ToRgba(swatch, Enumerable.Repeat((byte)255, 256).ToArray())));
							palettes++; break;
						case "excel_txt":
							ExcelTextTable.Parse(data); // validate before normalizing
							string text = Encoding.Latin1.GetString(data).Replace("\r\n", "\n");
							Write(TablePath(staging, name), Encoding.UTF8.GetBytes(text));
							tables++; break;
						case "dc6" when png && pngPalette is not null:
							var image = Dc6Image.Parse(data);
							for (int d = 0; d < image.Directions; d++)
								for (int f = 0; f < image.FramesPerDirection; f++)
								{
									var frame = image.DecodeFrame(d, f);
									if (frame.Width == 0 || frame.Height == 0) continue;
									if (!PngBudget() || !WritePng(PngPath(staging, name, d, f), PngWriter.EncodeIndexed(frame.Width, frame.Height, frame.Indices, pngPalette, frame.Opacity))) break;
								}
							break;
						case "dcc" when png && pngPalette is not null:
							var animation = DccAnimation.Parse(data);
							for (int d = 0; d < animation.Directions; d++)
							{
								var frames = animation.DecodeDirection(d);
								for (int f = 0; f < frames.Count; f++)
								{
									if (frames[f].Width == 0 || frames[f].Height == 0) continue;
									if (!PngBudget() || !WritePng(PngPath(staging, name, d, f), PngWriter.EncodeIndexed(frames[f].Width, frames[f].Height, frames[f].Indices, pngPalette))) break;
								}
							}
							break;
					}
				}
				catch (Exception e) when (e is InvalidDataException or ArgumentException or IOException or IndexOutOfRangeException or OverflowException)
				{
					if (errors.Count < MaxErrors) errors.Add($"{name}: {e.GetType().Name}: {e.Message}");
				}
			}
			var manifest = new { converter = ConverterVersion, source = sourceFingerprint, options = options.Signature, built = DateTimeOffset.UtcNow,
				resources, pngs, pngBytes, pngCapped = capped, tables, palettes, errors };
			File.WriteAllText(Path.Combine(staging, ManifestName), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
			RequireReplaceable(cacheDirectory); // re-check under the lock right before deleting
			if (Directory.Exists(cacheDirectory)) Directory.Delete(cacheDirectory, recursive: true);
			Directory.CreateDirectory(Path.GetDirectoryName(cacheDirectory)!);
			Directory.Move(staging, cacheDirectory);
		}
		catch { try { Directory.Delete(staging, true); } catch (IOException) { } throw; }
		return new(true, resources, pngs, tables, palettes, errors) { PngCapped = capped, PngBytes = pngBytes };
	}

	/// <summary>The target may be replaced only if it is missing, empty, or a cache this converter created.</summary>
	private static void RequireReplaceable(string cacheDirectory)
	{
		if (!Directory.Exists(cacheDirectory) || !Directory.EnumerateFileSystemEntries(cacheDirectory).Any() || IsOwnedCache(cacheDirectory)) return;
		throw new InvalidOperationException("Refusing to replace a folder that is not an OpenD2 asset cache: " + cacheDirectory);
	}
	private static FileStream AcquireLock(string path)
	{
		try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
		catch (IOException e) { throw new InvalidOperationException("Another asset cache build is using " + path, e); }
	}
	/// <summary>Mirrors the logical path below data\global\excel so tables with the same file name never collide.</summary>
	public static string TablePath(string root, string logicalPath)
	{
		var parts = SafeSegments(logicalPath);
		int start = parts.Length > 3 && parts[0] == "data" && parts[1] == "global" && parts[2] == "excel" ? 3 : 0;
		parts = parts[start..]; parts[^1] = Path.GetFileNameWithoutExtension(parts[^1]) + ".tsv";
		return Path.Combine([Path.GetFullPath(root), "tables", .. parts]);
	}
	private static string PngPath(string root, string logicalPath, int direction, int frame) =>
		Path.Combine([root, "png", .. SafeSegments(logicalPath), $"d{direction:D2}_f{frame:D3}.png"]);
	private static void Write(string path, byte[] data)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllBytes(path, data);
	}
}
