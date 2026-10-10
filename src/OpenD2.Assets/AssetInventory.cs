using System.Security.Cryptography;
using System.Text;

namespace OpenD2.Assets;

public sealed record AuditOptions(long MaxFileBytes = 268435456, long MaxTotalBytes = 4294967296, int MaxEntries = 100000, bool Decode = false, string Profile = "lod-1.10f");
public sealed record AssetEntry(string SourceArchive, string SourceVersion, string LogicalPath, string ContentId,
	string? ContentHash, long? Size, string Format, string DecoderVersion, string DecodeStatus,
	string RuntimeStatus, string? HdReplacementId, bool Selected, string? ErrorCode);
public sealed record ArchiveAudit(string SourceArchive, string? ArchiveHash, bool EnumerationComplete,
	int EnumeratedNames, string? ErrorCode);
public sealed record AssetReport(int SchemaVersion, InstallProbe Installation, IReadOnlyList<ArchiveAudit> Archives,
	IReadOnlyList<AssetEntry> Entries, bool Complete, string Coverage, long HashedBytes, long DecodeBytes);

public static class AssetInventory
{
	public static AssetReport Scan(string directory, AuditOptions? options = null, IEnumerable<string>? knownPaths = null)
	{
		options ??= new AuditOptions();
		if (options.MaxFileBytes < 0 || options.MaxTotalBytes < 0 || options.MaxEntries is < 1 or > 1000000)
			throw new ArgumentOutOfRangeException(nameof(options));
		var install = GameInstall.Probe(directory, options.Profile);
		var known = (knownPaths ?? []).Select(MpqArchive.NormalizePath).Take(options.MaxEntries + 1).ToArray();
		if (known.Length > options.MaxEntries) throw new InvalidDataException("Known-path list exceeds entry budget.");
		var archives = new List<ArchiveAudit>(); var entries = new List<AssetEntry>();
		var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		long bytes = 0, decodeBytes = 0;
		foreach (string path in install.Archives)
		{
			string name = Path.GetFileName(path); string? archiveHash = null;
			try
			{
				// Budget counts successfully hashed bytes (not all backend I/O). Original files are opened read-only.
				long length = new FileInfo(path).Length;
				if (length > options.MaxTotalBytes - bytes - decodeBytes) throw new InvalidDataException("Archive exceeds remaining I/O budget.");
				using (var source = File.OpenRead(path)) archiveHash = Convert.ToHexStringLower(SHA256.HashData(source));
				bytes += length;
				using var archive = new MpqArchive(path);
				var listed = archive.ListNames(Math.Max(1, options.MaxEntries - entries.Count));
				var names = listed.Concat(known).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal);
				foreach (var logical in names)
				{
					if (entries.Count >= options.MaxEntries) throw new InvalidDataException("Report entry budget exceeded.");
					string? hash = null, error = null; long? size = null;
					try
					{
						var value = archive.Hash(logical, Math.Min(options.MaxFileBytes, options.MaxTotalBytes - bytes - decodeBytes));
						size = value.Size; hash = value.Sha256; bytes += value.Size;
					}
					catch (MpqException ex) when (ex.Code == 2 && !listed.Contains(logical)) { continue; }
					catch (Exception ex) when (ex is IOException or InvalidDataException) { error = Error(ex); }
					string? kind = AssetDecoders.Kind(logical);
					bool opaqueDemoRecord = options.Profile == "demo-1.04" && DemoOpaqueRecords.IsRecord(name, logical);
					string decoder = kind is null ? "none" : AssetDecoders.Version;
					string decodeStatus = kind is null ? "not_implemented" : "not_requested";
					if (options.Decode && kind is not null)
					{
						decodeStatus = "read_failed";
						if (error is null)
						{
							try
							{
								var raw = archive.Read(logical, Math.Min(kind == "wav_pcm" ? PcmWave.MaxAuditInputBytes : AssetDecoders.MaxInputBytes, Math.Min(options.MaxFileBytes, options.MaxTotalBytes - bytes - decodeBytes)));
								decodeBytes += raw.Length;
								if (opaqueDemoRecord)
								{
									DemoOpaqueRecords.Validate(logical, raw);
									decodeStatus = "opaque_integrity_validated";
								}
								else { AssetDecoders.Validate(kind, raw); decodeStatus = "validated"; }
							}
							catch (Exception ex) when (ex is IOException or InvalidDataException)
							{ decodeStatus = "failed"; error = "decode_" + Error(ex); }
						}
					}
					entries.Add(new AssetEntry(name, install.VersionStatus, logical,
						Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(logical))), hash, size,
						opaqueDemoRecord ? "demo_opaque_record" : Path.GetExtension(logical.Replace('\\', '/')).TrimStart('.').ToLowerInvariant(), decoder, decodeStatus,
						"not_loaded", null, selected.Add(logical), error));
				}
				archives.Add(new ArchiveAudit(name, archiveHash, false, listed.Count, null));
			}
			catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
			{
				archives.Add(new ArchiveAudit(name, archiveHash, false, 0, Error(ex)));
			}
		}
		// Listfiles and supplied paths cannot establish that every unnamed hash-table entry is accounted for.
		return new AssetReport(1, install, archives, entries, false, "listfile-and-known-paths-only; version-and-total-coverage-unverified", bytes, decodeBytes);
	}
	private static string Error(Exception ex) => ex is MpqException mpq ? $"mpq_{mpq.Code}" : ex is InvalidDataException ? "budget_or_invalid_data" : ex.GetType().Name;
}
