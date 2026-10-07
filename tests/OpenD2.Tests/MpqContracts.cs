using System.Runtime.InteropServices;
using System.Security.Cryptography;
using OpenD2.Assets;

internal static class MpqContracts
{
	[DllImport("opend2_mpq", EntryPoint = "od2_fixture", CallingConvention = CallingConvention.Cdecl)]
	private static extern int Fixture([MarshalAs(UnmanagedType.LPUTF8Str)] string path,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string logical, byte[] data, uint size, int listfile);
	private static void Check(bool value) { if (!value) throw new Exception("MPQ assertion failed"); }
	private static void Throws<T>(Action action) where T : Exception
	{
		try { action(); } catch (T) { return; }
		throw new Exception($"Expected {typeof(T).Name}");
	}
	public static void Run(string root, Action<string, Action> test)
	{
		string dir = Path.Combine(root, "mpq-한글"); Directory.CreateDirectory(dir);
		byte[] data = Enumerable.Range(0, 180000).Select(i => (byte)(i % 251)).ToArray();
		string path = Path.Combine(dir, "d2data.mpq");
		test("native ABI and compressed encrypted multi-sector fixture", () =>
		{
			MpqArchive.VerifyBackend();
			Check(Fixture(path, "data\\global\\sample.bin", data, (uint)data.Length, 1) == 0);
			using var archive = new MpqArchive(path);
			Check(archive.Read("DATA/global/SAMPLE.bin").SequenceEqual(data));
			Check(archive.Hash("data/global/sample.bin", data.Length).Sha256 == Convert.ToHexStringLower(SHA256.HashData(data)));
			Check(archive.ListNames().Contains("data\\global\\sample.bin"));
		});
		test("read budgets and missing files", () =>
		{
			using var archive = new MpqArchive(path);
			Throws<InvalidDataException>(() => archive.Read("data/global/sample.bin", 32));
			Throws<MpqException>(() => archive.Read("absent.bin"));
			Throws<InvalidDataException>(() => archive.ListNames(1));
		});
		test("unsafe logical paths rejected", () =>
		{
			foreach (string value in new[] { "../bad", "data/../bad", "/bad", "C:\\bad", "a//b", "a\0b" })
				Throws<ArgumentException>(() => MpqArchive.NormalizePath(value));
		});
		test("dispose releases archive and rejects further reads", () =>
		{
			var archive = new MpqArchive(path); archive.Dispose(); archive.Dispose();
			Throws<ObjectDisposedException>(() => archive.Read("anything"));
			using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
		});
		test("invalid archives rejected", () =>
		{
			string broken = Path.Combine(root, "broken.mpq"); File.WriteAllBytes(broken, [77, 80, 81, 26]);
			Throws<MpqException>(() => { using var archive = new MpqArchive(broken); });
		});
		test("patch precedence and unsupported formats remain visible", () =>
		{
			Check(Fixture(Path.Combine(dir, "patch_d2.mpq"), "data\\global\\sample.bin", [1, 2, 3], 3, 1) == 0);
			var report = AssetInventory.Scan(dir);
			var entries = report.Entries.Where(e => e.LogicalPath == "data\\global\\sample.bin").ToArray();
			Check(entries.Length == 2 && entries[0].Selected && !entries[1].Selected);
			Check(entries[0].SourceArchive == "patch_d2.mpq" && entries[0].Size == 3);
			Check(entries.All(e => e.DecodeStatus == "not_implemented" && e.ErrorCode == null));
			Check(!report.Complete && report.Installation.VersionStatus == "unverified");
		});
		test("known paths recover entries without listfile", () =>
		{
			string hidden = Path.Combine(root, "hidden"); Directory.CreateDirectory(hidden);
			Check(Fixture(Path.Combine(hidden, "d2data.mpq"), "data\\hidden.bin", data, (uint)data.Length, 0) == 0);
			var report = AssetInventory.Scan(hidden, knownPaths: ["data/hidden.bin"]);
			Check(report.Entries.Any(e => e.LogicalPath == "data\\hidden.bin" && e.ContentHash != null));
			Check(!report.Complete);
		});
		test("MPQ decode inventory keeps hashes and reports malformed DC6", () =>
		{
			string decoded = Path.Combine(root, "decoded"); Directory.CreateDirectory(decoded);
			byte[] good = LegacyFormatContracts.Dc6(), bad = (byte[])good.Clone(); bad[60] = 127;
			Check(Fixture(Path.Combine(decoded, "patch_d2.mpq"), "sample.dc6", good, (uint)good.Length, 1) == 0);
			Check(Fixture(Path.Combine(decoded, "d2data.mpq"), "sample.dc6", bad, (uint)bad.Length, 1) == 0);
			byte[] before = File.ReadAllBytes(Path.Combine(decoded, "patch_d2.mpq"));
			Check(AssetInventory.Scan(decoded).Entries.Where(e => e.Format == "dc6").All(e => e.DecodeStatus == "not_requested"));
			var report = AssetInventory.Scan(decoded, new AuditOptions(Decode: true));
			var entries = report.Entries.Where(e => e.Format == "dc6").ToArray();
			Check(entries.Length == 2 && entries[0].DecodeStatus == "validated" && entries[0].Selected);
			Check(entries[1].DecodeStatus == "failed" && entries[1].ContentHash != null && entries[1].ErrorCode != null);
			Check(report.DecodeBytes == good.Length + bad.Length && !report.Complete);
			Check(AssetDecoders.ReadFromInstall(decoded, "sample.dc6").SequenceEqual(good));
			Check(File.ReadAllBytes(Path.Combine(decoded, "patch_d2.mpq")).SequenceEqual(before));
		});
		test("preview resource lookup fails on unreadable higher-priority archive", () =>
		{
			string decoded = Path.Combine(root, "decoded");
			File.WriteAllBytes(Path.Combine(decoded, "patch_d2.mpq"), [0, 1, 2]);
			Throws<MpqException>(() => AssetDecoders.ReadFromInstall(decoded, "sample.dc6"));
		});
		test("probe and inventory budgets report gaps", () =>
		{
			Check(GameInstall.Probe(dir).MissingArchives.Contains("d2exp.mpq"));
			Check(AssetInventory.Scan(dir, new AuditOptions(MaxTotalBytes: 0)).Archives.All(a => a.ErrorCode != null));
			Throws<InvalidDataException>(() => AssetInventory.Scan(dir, new AuditOptions(MaxEntries: 1), ["a", "a"]));
		});
	}
}
