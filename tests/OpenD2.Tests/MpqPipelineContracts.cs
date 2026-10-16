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

		test("asset cache converts DC6, palettes and tables, rebuilds only on source change, and is readable by the engine", () =>
		{
			var files = new Dictionary<string, byte[]>
			{
				["data\\global\\palette\\act1\\pal.dat"] = PaletteBytes(),
				["data\\global\\excel\\levels.txt"] = Encoding.Latin1.GetBytes("Name\tId\r\nRogue Encampment\t1\r\n"),
				["data\\global\\ui\\panel\\sample.dc6"] = LegacyFormatContracts.Dc6(),
				["data\\global\\ui\\panel\\broken.dc6"] = [1, 2, 3],
				["data\\global\\sfx\\ignored.wav"] = [0],
			};
			string cache = Path.Combine(root, "asset-cache", "1.0t");
			using var source = new MemorySource(files);
			var first = AssetCache.Build(source, "fingerprint-1", cache);
			Check(first.Rebuilt && first.Palettes == 1 && first.Tables == 1 && first.PngFiles == 1 && first.Errors.Count == 1 && first.Errors[0].Contains("broken.dc6"), "first build");
			Check(File.Exists(Path.Combine(cache, "png", "data", "global", "ui", "panel", "sample.dc6", "d00_f000.png")), "dc6 png");
			Check(File.Exists(Path.Combine(cache, "palettes", "act1.png")), "palette swatch");
			Check(File.ReadAllText(Path.Combine(cache, "tables", "levels.tsv")) == "Name\tId\nRogue Encampment\t1\n", "normalized table");
			Check(!File.Exists(AssetCache.RawPath(cache, "data\\global\\sfx\\ignored.wav")), "audio is opt-in");
			Check(!AssetCache.Build(source, "fingerprint-1", cache).Rebuilt, "unchanged source is not rebuilt");
			Check(AssetCache.Build(source, "fingerprint-1", cache, new AssetCacheOptions { PngKinds = new HashSet<string>() }).Rebuilt, "option change rebuilds");
			files["data\\global\\excel\\levels.txt"] = Encoding.Latin1.GetBytes("Name\tId\r\nLut Gholein\t40\r\n");
			Check(AssetCache.Build(source, "fingerprint-2", cache).Rebuilt, "changed source hash rebuilds");
			Check(Encoding.Latin1.GetString(AssetDecoders.ReadFromInstall(cache, "DATA/GLOBAL/EXCEL/LEVELS.TXT")).Contains("Lut Gholein"), "engine reads converted cache");
			try { AssetDecoders.ReadFromInstall(cache, "data\\global\\excel\\missing.txt"); throw new Exception("missing resource read"); } catch (FileNotFoundException) { }
			Check(!Directory.EnumerateDirectories(Path.GetDirectoryName(cache)!).Any(d => d.Contains(".building-")), "no staging leftovers");
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
