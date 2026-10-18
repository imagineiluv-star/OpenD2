using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using OpenD2.Assets;

// P1-MPQ pipeline. Synthetic fixtures only: runs everywhere, including CI, with no game data.
internal static class MpqPipelineContracts
{
	private sealed class MemorySource(Dictionary<string, byte[]> files) : IAssetSource
	{
		public IReadOnlyList<string> List() => files.Keys.Order(StringComparer.Ordinal).ToArray();
		public bool TryRead(string path, long maxBytes, out byte[] data) => files.TryGetValue(MpqArchive.NormalizePath(path), out data!);
		public void Dispose() { }
	}
	// Throws for chosen paths, like a chmod 000 file behind a directory source.
	private sealed class DenyingSource(Dictionary<string, byte[]> files, params string[] denied) : IAssetSource
	{
		public IReadOnlyList<string> List() => files.Keys.Concat(denied).Order(StringComparer.Ordinal).ToArray();
		public bool TryRead(string path, long maxBytes, out byte[] data)
		{
			path = MpqArchive.NormalizePath(path);
			if (denied.Contains(path)) throw new UnauthorizedAccessException($"Access to the path '{path}' is denied.");
			return files.TryGetValue(path, out data!);
		}
		public void Dispose() { }
	}
	internal static byte[] PaletteBytes() { var p = new byte[768]; for (int i = 0; i < 256; i++) { p[i * 3] = (byte)i; p[i * 3 + 1] = (byte)(255 - i); p[i * 3 + 2] = 7; } return p; }

	public static void Run(string root, Action<string, Action> test)
	{
		static void Check(bool value, string what = "") { if (!value) throw new Exception("Assertion failed " + what); }

		test("MPQ manifest ships 1.11b hashes and a 1.14d placeholder", () =>
		{
			var versions = InstallManifest.Default.Versions;
			var v111 = versions.Single(v => v.Id == "1.11b");
			Check(!v111.Placeholder && v111.Files.Count == 10 && v111.Files.ContainsKey("d2data.mpq") && v111.Files.Values.All(f => f.Sha256.Length == 64 && f.Size > 0));
			Check(versions.Single(v => v.Id == "1.14d") is { Placeholder: true, Files.Count: 0 });
			try { InstallManifest.Parse("{\"schema\":1,\"versions\":[{\"id\":\"x\",\"description\":\"\",\"placeholder\":false,\"files\":{\"a.mpq\":{\"sha256\":\"zz\",\"size\":1}}}]}"); throw new Exception("bad hash accepted"); }
			catch (InvalidDataException) { }
			try { InstallManifest.Parse("{\"schema\":1,\"versions\":[{\"id\":\"x\",\"description\":\"\",\"placeholder\":false,\"files\":{}}]}"); throw new Exception("empty non-placeholder accepted"); }
			catch (InvalidDataException) { }
		});

		test("install verification matches, rejects tampering and reuses the hash cache", () =>
		{
			string dir = Path.Combine(root, "verify-install"); Directory.CreateDirectory(dir);
			byte[] a = Encoding.ASCII.GetBytes("synthetic archive a"), b = Encoding.ASCII.GetBytes("synthetic archive b");
			File.WriteAllBytes(Path.Combine(dir, "d2data.mpq"), a); File.WriteAllBytes(Path.Combine(dir, "Patch_D2.mpq"), b);
			string H(byte[] x) => Convert.ToHexStringLower(SHA256.HashData(x));
			string Entry(string name, byte[] x) => $"\"{name}\":{{\"sha256\":\"{H(x)}\",\"size\":{x.Length}}}";
			var manifest = InstallManifest.Parse("{\"schema\":1,\"versions\":[{\"id\":\"9.99z\",\"description\":\"\",\"placeholder\":true,\"files\":{}},"
				+ "{\"id\":\"1.0t\",\"description\":\"\",\"placeholder\":false,\"files\":{" + Entry("d2data.mpq", a) + "," + Entry("patch_d2.mpq", b) + "}}]}");
			string hashes = Path.Combine(root, "verify-hashes.json");
			var ok = manifest.Verify(dir, hashes);
			Check(ok.Verified && ok.Version == "1.0t" && ok.Missing.Count == 0 && ok.Unknown.Count == 0, "match");
			Check(File.ReadAllText(hashes).Contains(H(a)), "hash cache written");
			// Forged hash cache: same name/size/mtime, different bytes. Default verification ignores the cache and catches it.
			var info = new FileInfo(Path.Combine(dir, "d2data.mpq")); var mtime = info.LastWriteTimeUtc;
			File.WriteAllBytes(info.FullName, Encoding.ASCII.GetBytes("synthetic archive X")); File.SetLastWriteTimeUtc(info.FullName, mtime);
			Check(!manifest.Verify(dir).Verified, "full hash catches in-place change");
			Check(manifest.Verify(dir, hashes).Verified, "documented weakness: opt-in size+mtime cache trusts the forged entry");
			File.WriteAllBytes(info.FullName, a); File.SetLastWriteTimeUtc(info.FullName, mtime);
			File.WriteAllBytes(Path.Combine(dir, "d2data.mpq"), Encoding.ASCII.GetBytes("synthetic archive A"));
			var bad = manifest.Verify(dir, hashes);
			Check(!bad.Verified && bad.Mismatched.SequenceEqual(["d2data.mpq"]), "tamper detected");
			File.Delete(Path.Combine(dir, "Patch_D2.mpq"));
			Check(manifest.Verify(dir).Missing.Contains("patch_d2.mpq"), "missing reported");
			Check(!InstallManifest.Default.Verify(dir).Verified, "unknown install is not verified");
		});

		test("PNG writer emits valid chunks, CRCs and pixel rows", () =>
		{
			byte[] rgba = [255, 0, 0, 255, 0, 255, 0, 128, 0, 0, 255, 0, 1, 2, 3, 4];
			var png = PngWriter.EncodeRgba(2, 2, rgba);
			Check(png.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }), "signature");
			int at = 8; var idat = new MemoryStream(); var types = new List<string>();
			while (at < png.Length)
			{
				int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at)); string type = Encoding.ASCII.GetString(png, at + 4, 4); types.Add(type);
				uint crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at + 8 + length));
				Check(crc == PngWriter.Crc32(png.AsSpan(at + 4, 4 + length)), "crc " + type);
				if (type == "IHDR") Check(BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at + 8)) == 2 && png[at + 16] == 8 && png[at + 17] == 6, "ihdr");
				if (type == "IDAT") idat.Write(png, at + 8, length);
				at += 12 + length;
			}
			Check(types.SequenceEqual(["IHDR", "IDAT", "IEND"]), "chunk order");
			idat.Position = 0; using var z = new ZLibStream(idat, CompressionMode.Decompress); var rows = new MemoryStream(); z.CopyTo(rows);
			byte[] expected = [0, .. rgba[..8], 0, .. rgba[8..]];
			Check(rows.ToArray().SequenceEqual(expected), "rows");
			try { PngWriter.EncodeRgba(2, 2, rgba[..15]); throw new Exception("short buffer accepted"); } catch (ArgumentException) { }
		});

		test("asset cache records skipped PNGs when their palette cannot be read", () =>
		{
			var files = new Dictionary<string, byte[]>
			{
				["data\\global\\palette\\units\\pal.dat"] = PaletteBytes(),
				["data\\global\\tiles\\act3\\jungle\\a.dc6"] = LegacyFormatContracts.Dc6(),
				["data\\global\\tiles\\act3\\jungle\\b.dc6"] = LegacyFormatContracts.Dc6(),
				["data\\global\\ui\\panel\\sample.dc6"] = LegacyFormatContracts.Dc6(),
			};
			using var source = new MemorySource(files);
			var result = AssetCache.Build(source, "fingerprint-missing-palette", Path.Combine(root, "asset-cache-missing-palette", "1.0t"));
			Check(result.PngFiles == 1, $"only the units PNG converts ({result.PngFiles})");
			Check(result.Errors.Count == 1 && result.Errors[0].Contains("act3") && result.Errors[0].Contains("2 PNG conversion(s) skipped"), string.Join(" | ", result.Errors));
		});

		test("asset cache records a broken (wrong-size) palette and skips only the PNGs that need it", () =>
		{
			var files = new Dictionary<string, byte[]>
			{
				["data\\global\\palette\\units\\pal.dat"] = PaletteBytes(),
				["data\\global\\palette\\act2\\pal.dat"] = PaletteBytes()[..767],
				["data\\global\\tiles\\act2\\desert\\a.dc6"] = LegacyFormatContracts.Dc6(),
				["data\\global\\ui\\panel\\sample.dc6"] = LegacyFormatContracts.Dc6(),
			};
			using var source = new MemorySource(files);
			var result = AssetCache.Build(source, "fingerprint-broken-palette", Path.Combine(root, "asset-cache-broken-palette", "1.0t"));
			Check(result.PngFiles == 1, $"only the units PNG converts ({result.PngFiles})");
			Check(result.Errors.Any(e => e.Contains("act2\\pal.dat could not be read") && e.Contains("1 PNG conversion(s) skipped")), string.Join(" | ", result.Errors));
		});

		test("asset cache records permission-denied resources and keeps building", () =>
		{
			var files = new Dictionary<string, byte[]>
			{
				["data\\global\\palette\\units\\pal.dat"] = PaletteBytes(),
				["data\\global\\ui\\panel\\sample.dc6"] = LegacyFormatContracts.Dc6(),
				["data\\global\\excel\\levels.txt"] = Encoding.Latin1.GetBytes("Name\tId\r\nRogue Encampment\t1\r\n"),
			};
			using var source = new DenyingSource(files, "data\\global\\ui\\panel\\locked.dc6", "data\\global\\palette\\act1\\pal.dat");
			var result = AssetCache.Build(source, "fingerprint-denied", Path.Combine(root, "asset-cache-denied", "1.0t"));
			Check(result.Rebuilt && result.PngFiles == 1 && result.Tables == 1, $"build continued ({result.PngFiles} PNG, {result.Tables} tables)");
			Check(result.Errors.Any(e => e.Contains("locked.dc6") && e.Contains("UnauthorizedAccessException")), string.Join(" | ", result.Errors));
			Check(result.Errors.Any(e => e.Contains("act1\\pal.dat") && e.Contains("UnauthorizedAccessException")), string.Join(" | ", result.Errors));
		});

		test("asset cache converts DC6, palettes and tables, rebuilds only on source change, and is readable by the engine", () =>
		{
			var files = new Dictionary<string, byte[]>
			{
				["data\\global\\palette\\act1\\pal.dat"] = PaletteBytes(),
				["data\\global\\palette\\units\\pal.dat"] = PaletteBytes().Select(b => (byte)(b ^ 0x0f)).ToArray(),
				["data\\global\\tiles\\act1\\town\\floor.dc6"] = LegacyFormatContracts.Dc6(),
				["data\\global\\excel\\levels.txt"] = Encoding.Latin1.GetBytes("Name\tId\r\nRogue Encampment\t1\r\n"),
				["data\\global\\ui\\panel\\sample.dc6"] = LegacyFormatContracts.Dc6(),
				["data\\global\\ui\\panel\\broken.dc6"] = [1, 2, 3],
				["data\\global\\sfx\\ignored.wav"] = [0],
			};
			string cache = Path.Combine(root, "asset-cache", "1.0t");
			using var source = new MemorySource(files);
			var first = AssetCache.Build(source, "fingerprint-1", cache);
			Check(first.Rebuilt && first.Palettes == 2 && first.Tables == 1 && first.PngFiles == 2 && first.Errors.Count == 1 && first.Errors[0].Contains("broken.dc6"), "first build");
			Check(File.Exists(Path.Combine(cache, "png", "data", "global", "ui", "panel", "sample.dc6", "d00_f000.png")), "dc6 png");
			Check(File.Exists(Path.Combine(cache, "palettes", "act1.png")), "palette swatch");
			// P2-2: no fixed act1; UI art converts with units, act tiles with their act (palette is part of the cache signature).
			string manifestText = File.ReadAllText(Path.Combine(cache, AssetCache.ManifestName));
			Check(manifestText.Contains("\"units\": ") && manifestText.Contains("\"act1\": 1") && manifestText.Contains("auto:" + ScreenPalettes.RulesVersion), "per-resource PNG palettes " + manifestText);
			var uiPng = PngReader.DecodeRgb(File.ReadAllBytes(Path.Combine(cache, "png", "data", "global", "ui", "panel", "sample.dc6", "d00_f000.png")));
			var tilePng = PngReader.DecodeRgb(File.ReadAllBytes(Path.Combine(cache, "png", "data", "global", "tiles", "act1", "town", "floor.dc6", "d00_f000.png")));
			Check(!uiPng.Rgb.AsSpan().SequenceEqual(tilePng.Rgb), "same frame, different palettes");
			Check(new AssetCacheOptions { PngPalette = "act1" }.Signature != new AssetCacheOptions().Signature, "palette in cache key");
			Check(File.ReadAllText(Path.Combine(cache, "tables", "levels.tsv")) == "Name\tId\nRogue Encampment\t1\n", "normalized table");
			Check(AssetCache.TablePath(cache, "data\\global\\excel\\levels.txt") != AssetCache.TablePath(cache, "data\\global\\excel\\expansion\\levels.txt")
				&& AssetCache.TablePath(cache, "data\\global\\excel\\expansion\\levels.txt").EndsWith(Path.Combine("tables", "expansion", "levels.tsv")), "same-named tables do not collide");
			Check(!File.Exists(AssetCache.RawPath(cache, "data\\global\\sfx\\ignored.wav")), "audio is opt-in");
			Check(!AssetCache.Build(source, "fingerprint-1", cache).Rebuilt, "unchanged source is not rebuilt");
			Check(AssetCache.Build(source, "fingerprint-1", cache, new AssetCacheOptions { PngKinds = new HashSet<string>() }).Rebuilt, "option change rebuilds");
			files["data\\global\\excel\\levels.txt"] = Encoding.Latin1.GetBytes("Name\tId\r\nLut Gholein\t40\r\n");
			Check(AssetCache.Build(source, "fingerprint-2", cache).Rebuilt, "changed source hash rebuilds");
			Check(Encoding.Latin1.GetString(AssetDecoders.ReadFromInstall(cache, "DATA/GLOBAL/EXCEL/LEVELS.TXT")).Contains("Lut Gholein"), "engine reads converted cache");
			try { AssetDecoders.ReadFromInstall(cache, "data\\global\\excel\\missing.txt"); throw new Exception("missing resource read"); } catch (FileNotFoundException) { }
			Check(!Directory.EnumerateDirectories(Path.GetDirectoryName(cache)!).Any(d => d.Contains(".building-")), "no staging leftovers");
		});

		test("asset cache replaces only empty or OpenD2-owned folders, never user folders", () =>
		{
			string temp = Path.GetFullPath(Path.GetTempPath());
			string Under(string name) { string p = Path.GetFullPath(Path.Combine(root, "replace-guard", name)); Check(p.StartsWith(temp, StringComparison.Ordinal), "test path under temp"); return p; }
			using var source = new MemorySource(new() { ["data\\global\\palette\\act1\\pal.dat"] = PaletteBytes() });
			string user = Under("user-folder"); Directory.CreateDirectory(Path.Combine(user, "docs"));
			File.WriteAllText(Path.Combine(user, "docs", "keep.txt"), "user data"); File.WriteAllText(Path.Combine(user, "notes.txt"), "user data");
			try { AssetCache.Build(source, "fp", user); throw new Exception("user folder replaced"); } catch (InvalidOperationException) { }
			Check(File.ReadAllText(Path.Combine(user, "docs", "keep.txt")) == "user data" && File.Exists(Path.Combine(user, "notes.txt")), "user files survive");
			string foreign = Under("foreign-manifest"); Directory.CreateDirectory(foreign);
			File.WriteAllText(Path.Combine(foreign, AssetCache.ManifestName), "{\"tool\":\"someone else\"}"); File.WriteAllText(Path.Combine(foreign, "data.bin"), "x");
			try { AssetCache.Build(source, "fp", foreign); throw new Exception("foreign manifest folder replaced"); } catch (InvalidOperationException) { }
			Check(File.Exists(Path.Combine(foreign, "data.bin")), "foreign folder survives");
			string empty = Under("empty"); Directory.CreateDirectory(empty);
			Check(AssetCache.Build(source, "fp", empty).Rebuilt && AssetCache.IsOwnedCache(empty), "empty folder becomes a cache");
			Check(AssetCache.Build(source, "fp2", empty).Rebuilt, "owned cache is replaced on source change");
			Check(!Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(empty)!).Any(e => e.Contains(".building-") || e.EndsWith(".lock")), "no staging or lock leftovers");
			using (new FileStream(Under("locked") + ".lock", FileMode.Create, FileAccess.ReadWrite, FileShare.None))
			{
				try { AssetCache.Build(source, "fp", Under("locked")); throw new Exception("concurrent build allowed"); } catch (InvalidOperationException) { }
			}
			try { AssetCache.Build(source, "fp", Path.GetPathRoot(temp)!); throw new Exception("root accepted"); } catch (Exception e) when (e is ArgumentException or InvalidOperationException) { }
		});

		test("asset cache rejects Windows device names in resource paths", () =>
		{
			foreach (var name in new[] { "CON", "nul.dc6", "Aux", "com1.txt", "LPT9", "prn " }) Check(AssetCache.IsDeviceName(name), name);
			foreach (var name in new[] { "console.dc6", "com10", "lpt", "nullify.txt", "act1" }) Check(!AssetCache.IsDeviceName(name), name);
			var files = new Dictionary<string, byte[]> { ["data\\global\\palette\\act1\\pal.dat"] = PaletteBytes(),
				["data\\global\\ui\\con.dc6"] = LegacyFormatContracts.Dc6(), ["data\\global\\aux\\panel.dc6"] = LegacyFormatContracts.Dc6() };
			string cache = Path.Combine(root, "device-cache");
			var result = AssetCache.Build(new MemorySource(files), "fp", cache);
			Check(result.Errors.Count == 2 && result.Errors.All(e => e.Contains("device name")), "device paths reported");
			Check(!Directory.Exists(Path.Combine(cache, "raw", "data", "global", "aux")) && !File.Exists(Path.Combine(cache, "raw", "data", "global", "ui", "con.dc6")), "nothing written");
			try { AssetCache.RawPath(cache, "data\\nul\\x.txt"); throw new Exception("device path accepted"); } catch (ArgumentException) { }
		});

		test("cache rejects trailing dot/space names and extra device names, and caps total PNG output", () =>
		{
			foreach (var name in new[] { "COM0", "lpt0.txt", "CONIN$", "conout$.dc6", "com\u00b9", "panel.", "panel ", "" }) Check(AssetCache.IsUnsafeSegment(name), "unsafe " + name);
			foreach (var name in new[] { "panel.dc6", "com10", "conin", "a.b" }) Check(!AssetCache.IsUnsafeSegment(name), "safe " + name);
			try { AssetCache.RawPath(root, "data\\global\\ui.\\a.dc6"); throw new Exception("trailing dot accepted"); } catch (ArgumentException) { }
			try { AssetCache.RawPath(root, "data\\global\\CONOUT$\\a.dc6"); throw new Exception("CONOUT$ accepted"); } catch (ArgumentException) { }
			var files = new Dictionary<string, byte[]> { ["data\\global\\palette\\units\\pal.dat"] = PaletteBytes() };
			for (int i = 0; i < 5; i++) files[$"data\\global\\ui\\p{i}.dc6"] = LegacyFormatContracts.Dc6();
			string cache = Path.Combine(root, "png-cap");
			var capped = AssetCache.Build(new MemorySource(files), "fp", cache, new AssetCacheOptions { MaxPngFiles = 3 });
			Check(capped.PngFiles == 3 && capped.PngCapped && capped.PngBytes > 0 && capped.Errors.Count(e => e.Contains("PNG output cap")) == 1 && capped.Resources == 6, "file cap");
			Check(!AssetCache.IsCurrent(cache, "fp", new AssetCacheOptions { MaxPngFiles = 3 }), "capped cache is stale");
			var retry = AssetCache.Build(new MemorySource(files), "fp", cache, new AssetCacheOptions { MaxPngFiles = 3 });
			Check(retry.Rebuilt && retry.PngCapped, "capped cache rebuilt with the same options");
			var bytesCap = AssetCache.Build(new MemorySource(files), "fp", cache, new AssetCacheOptions { MaxPngBytes = 1 });
			Check(bytesCap.PngFiles == 0 && bytesCap.PngBytes == 0 && bytesCap.PngCapped, "byte cap is checked before writing");
			var free = AssetCache.Build(new MemorySource(files), "fp", cache);
			Check(free.PngFiles == 5 && !free.PngCapped, "default cap not hit");
			Check(AssetCache.IsCurrent(cache, "fp", new AssetCacheOptions()) && !AssetCache.Build(new MemorySource(files), "fp", cache).Rebuilt, "uncapped cache stays current");
		});

		test("--include prefixes match on path-separator boundaries", () =>
		{
			foreach (var prefix in new[] { "data", "data\\", "data/" })
			{
				var include = AssetCacheOptions.PathPrefix(prefix);
				Check(include("data\\global\\excel\\levels.txt") && include("data"), "inside " + prefix);
				Check(!include("database\\x.txt") && !include("dat") && !include("datax"), "sibling " + prefix);
			}
			var excel = AssetCacheOptions.PathPrefix("data\\global\\excel\\");
			Check(excel("data\\global\\excel\\armor.txt") && !excel("data\\global\\excel2\\armor.txt") && !excel("data\\global\\exce"), "nested prefix");
		});

		test("data and cache paths come from settings or environment and the cache refuses Git checkouts", () =>
		{
			string? previous = Environment.GetEnvironmentVariable(D2DataPaths.DataPathVariable);
			try
			{
				Environment.SetEnvironmentVariable(D2DataPaths.DataPathVariable, null);
				Check(D2DataPaths.ResolveDataPath() is null, "unset");
				Environment.SetEnvironmentVariable(D2DataPaths.DataPathVariable, root);
				Check(D2DataPaths.ResolveDataPath() == Path.GetFullPath(root), "env");
				Check(D2DataPaths.ResolveDataPath(Path.GetTempPath()) == Path.GetFullPath(Path.GetTempPath()), "explicit wins");
			}
			finally { Environment.SetEnvironmentVariable(D2DataPaths.DataPathVariable, previous); }
			string repo = Path.Combine(root, "fake-repo"); Directory.CreateDirectory(Path.Combine(repo, ".git"));
			try { D2DataPaths.ResolveCachePath(Path.Combine(repo, "asset-cache")); throw new Exception("cache inside Git accepted"); }
			catch (InvalidOperationException) { }
			try { AssetCache.Build(new MemorySource([]), "x", Path.Combine(repo, "cache")); throw new Exception("build inside Git accepted"); }
			catch (InvalidOperationException) { }
		});
	}
}
