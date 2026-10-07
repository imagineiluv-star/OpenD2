using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OpenD2.Assets;

public sealed class MpqException(string operation, int code) : IOException($"{operation}: StormLib error {code}")
{
	public int Code { get; } = code;
}

// Single-owner, synchronous reader. Never writes to or extracts into the source directory.
public sealed class MpqArchive : IDisposable
{
	public static void VerifyBackend()
	{
		if (Native.Version() != 1) throw new InvalidDataException("Unsupported MPQ backend ABI.");
	}
	private readonly ArchiveHandle archive;
	public MpqArchive(string path)
	{
		Check(Native.Open(Path.GetFullPath(path), out var handle), "Open archive");
		archive = new ArchiveHandle(handle);
	}
	public static string NormalizePath(string path)
	{
		if (string.IsNullOrWhiteSpace(path) || path.Length > 1023 || path.Any(char.IsControl))
			throw new ArgumentException("Invalid MPQ logical path.");
		path = path.Replace('/', '\\');
		if (path.StartsWith('\\') || path.Contains(':') || path.Split('\\').Any(p => p is "" or "." or ".."))
			throw new ArgumentException("MPQ paths must be relative and cannot traverse directories.");
		return path.ToLowerInvariant();
	}
	public IReadOnlyList<string> ListNames(int limit = 100000)
	{
		if (limit is < 1 or > 1000000) throw new ArgumentOutOfRangeException(nameof(limit));
		ObjectDisposedException.ThrowIf(archive.IsClosed, this);
		int code = Native.FindOpen(archive, out var cursor);
		if (code == 2 || code == 18) return [];
		Check(code, "Enumerate archive");
		using var find = new FindHandle(cursor);
		var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var buffer = new byte[4096];
		int entries = 0;
		do
		{
			if (++entries > limit) throw new InvalidDataException("Archive entry limit exceeded.");
			Check(Native.FindName(find, buffer, (uint)buffer.Length), "Read entry name");
			int end = Array.IndexOf(buffer, (byte)0);
			if (end < 0) throw new InvalidDataException("Unterminated MPQ filename.");
			// A strict decoder avoids silently merging distinct invalid filenames.
			names.Add(NormalizePath(new UTF8Encoding(false, true).GetString(buffer, 0, end)));
			code = Native.FindNext(find);
		} while (code == 0);
		if (code != 18 && code != 2) Check(code, "Continue enumeration");
		return names.Order(StringComparer.Ordinal).ToArray();
	}
	public byte[] Read(string logicalPath, long maxBytes = 256L * 1024 * 1024)
	{
		using var file = OpenFile(logicalPath, out var size);
		ValidateSize(size, maxBytes);
		var bytes = new byte[checked((int)size)];
		if (bytes.Length > 0) ReadExact(file, bytes, bytes.Length);
		return bytes;
	}
	public (long Size, string Sha256) Hash(string logicalPath, long maxBytes)
	{
		using var file = OpenFile(logicalPath, out var size);
		ValidateSize(size, maxBytes);
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		var buffer = new byte[65536];
		long remaining = (long)size;
		while (remaining > 0)
		{
			int count = (int)Math.Min(remaining, buffer.Length);
			ReadExact(file, buffer, count); hash.AppendData(buffer, 0, count); remaining -= count;
		}
		return ((long)size, Convert.ToHexStringLower(hash.GetHashAndReset()));
	}
	private FileHandle OpenFile(string path, out ulong size)
	{
		ObjectDisposedException.ThrowIf(archive.IsClosed, this);
		Check(Native.FileOpen(archive, NormalizePath(path), out var file, out size), "Open file");
		return new FileHandle(file);
	}
	private static void ValidateSize(ulong size, long maxBytes)
	{
		if (maxBytes < 0 || size > (ulong)maxBytes || size > int.MaxValue)
			throw new InvalidDataException("File size exceeds configured read budget.");
	}
	private static void ReadExact(FileHandle file, byte[] buffer, int count)
	{
		Check(Native.Read(file, buffer, (uint)count, out uint read), "Read file");
		if (read != count) throw new EndOfStreamException("MPQ entry ended before its declared size.");
	}
	internal static void Check(int code, string operation) { if (code != 0) throw new MpqException(operation, code); }
	public void Dispose() => archive.Dispose();

	private sealed class ArchiveHandle : SafeHandleZeroOrMinusOneIsInvalid
	{
		public ArchiveHandle(nint value) : base(true) { SetHandle(value); }
		protected override bool ReleaseHandle() { Native.Close(handle); return true; }
	}
	private sealed class FileHandle : SafeHandleZeroOrMinusOneIsInvalid
	{
		public FileHandle(nint value) : base(true) { SetHandle(value); }
		protected override bool ReleaseHandle() { Native.FileClose(handle); return true; }
	}
	private sealed class FindHandle : SafeHandleZeroOrMinusOneIsInvalid
	{
		public FindHandle(nint value) : base(true) { SetHandle(value); }
		protected override bool ReleaseHandle() { Native.FindClose(handle); return true; }
	}
	private static class Native
	{
		private const string Lib = "opend2_mpq";
		[DllImport(Lib, EntryPoint = "od2_abi_version", CallingConvention = CallingConvention.Cdecl)] internal static extern int Version();
		[DllImport(Lib, EntryPoint = "od2_open", CallingConvention = CallingConvention.Cdecl)]
		internal static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out nint handle);
		[DllImport(Lib, EntryPoint = "od2_close", CallingConvention = CallingConvention.Cdecl)] internal static extern void Close(nint handle);
		[DllImport(Lib, EntryPoint = "od2_file_open", CallingConvention = CallingConvention.Cdecl)]
		internal static extern int FileOpen(ArchiveHandle archive, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, out nint file, out ulong size);
		[DllImport(Lib, EntryPoint = "od2_read", CallingConvention = CallingConvention.Cdecl)] internal static extern int Read(FileHandle file, [Out] byte[] data, uint length, out uint read);
		[DllImport(Lib, EntryPoint = "od2_file_close", CallingConvention = CallingConvention.Cdecl)] internal static extern void FileClose(nint handle);
		[DllImport(Lib, EntryPoint = "od2_find_open", CallingConvention = CallingConvention.Cdecl)] internal static extern int FindOpen(ArchiveHandle archive, out nint cursor);
		[DllImport(Lib, EntryPoint = "od2_find_name", CallingConvention = CallingConvention.Cdecl)] internal static extern int FindName(FindHandle cursor, [Out] byte[] name, uint capacity);
		[DllImport(Lib, EntryPoint = "od2_find_next", CallingConvention = CallingConvention.Cdecl)] internal static extern int FindNext(FindHandle cursor);
		[DllImport(Lib, EntryPoint = "od2_find_close", CallingConvention = CallingConvention.Cdecl)] internal static extern void FindClose(nint cursor);
	}
}
