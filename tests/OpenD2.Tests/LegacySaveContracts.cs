using System.Buffers.Binary;
using OpenD2.Assets;

internal static class LegacySaveContracts
{
	private static void Check(bool value) { if (!value) throw new Exception("Legacy save assertion failed."); }
	private static void Reject(byte[] bytes) { try { LegacySaveInspector.Parse(bytes); } catch (InvalidDataException) { return; } throw new Exception("Accepted invalid save."); }
	private static byte[] Fixture()
	{
		var bytes = new byte[335]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0xaa55aa55); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 96);
		BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 335); "Hero"u8.CopyTo(bytes.AsSpan(20)); bytes[36] = 32; bytes[40] = 1; bytes[43] = 12;
		// Independent Python uint32 vector; synthetic header only, not a full character fixture.
		BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 2478257282); return bytes;
	}
	public static void Run(string root, Action<string, Action> test)
	{
		test("Legacy v96 inspection reports header and checksum without claiming import or body validation", () =>
		{
			var preview = LegacySaveInspector.Parse(Fixture()); Check(preview.Version == 96 && preview.Name == "Hero" && preview.ClassId == 1 && preview.Level == 12);
			Check(preview.Expansion && !preview.Hardcore && !preview.Dead && preview.ChecksumValid && !preview.BodyValidated && !preview.ImportSupported);
		});
		test("Legacy inspection rejects truncated, oversized, mismatched-length and damaged files", () =>
		{
			Reject(Fixture()[..334]); Reject(new byte[LegacySaveInspector.MaxBytes + 1]);
			var bytes = Fixture(); bytes[8]++; Reject(bytes); bytes = Fixture(); bytes[^1]++; Reject(bytes);
		});
		test("Legacy inspection rejects other versions and wrong magic instead of guessing D2R layout", () =>
		{
			foreach (byte version in new byte[] { 89, 92, 97, 98, 99 }) { var bytes = Fixture(); bytes[4] = version; Reject(bytes); }
			var bad = Fixture(); bad[0] = 0; Reject(bad);
		});
		test("Legacy file inspection is read-only and creates no converted character", () =>
		{
			var path = Path.Combine(root, "synthetic.d2s"); var bytes = Fixture(); File.WriteAllBytes(path, bytes);
			Check(LegacySaveInspector.Read(path).FileBytes == 335 && File.ReadAllBytes(path).SequenceEqual(bytes));
		});
	}
}
