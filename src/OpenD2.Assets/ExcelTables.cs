using System.Globalization;
using System.Text;

namespace OpenD2.Assets;

// Diablo's Excel TXT files are tab-delimited, not CSV. Quotes are literal.
public sealed class ExcelTextTable
{
	public const int MaxBytes = 8388608, MaxRows = 65536, MaxColumns = 512, MaxCells = 1048576;
	public IReadOnlyList<string> Columns { get; }
	public IReadOnlyList<IReadOnlyList<string>> Rows { get; }
	public IReadOnlyList<int> LineNumbers { get; }
	private ExcelTextTable(string[] columns, List<IReadOnlyList<string>> rows, List<int> lines)
	{ Columns = Array.AsReadOnly(columns); Rows = rows.AsReadOnly(); LineNumbers = lines.AsReadOnly(); }
	public static ExcelTextTable Parse(ReadOnlySpan<byte> data)
	{
		AssetBinary.Require(data.Length is > 0 and <= MaxBytes, "TXT input exceeds size bounds.");
		string text;
		try { text = data.StartsWith(new byte[] { 239, 187, 191 }) ? new UTF8Encoding(false, true).GetString(data[3..]) : Encoding.Latin1.GetString(data); }
		catch (DecoderFallbackException e) { throw new InvalidDataException("Invalid UTF-8 TXT.", e); }
		AssetBinary.Require(!text.Contains('\0'), "TXT contains NUL (UTF-16 is unsupported).");
		using var reader = new StringReader(text);
		string[] columns = (reader.ReadLine() ?? "").Split('\t');
		AssetBinary.Require(columns.Length <= MaxColumns && columns.Any(c => c.Length > 0) && columns.All(c => c.Length <= 256), "Invalid TXT header.");
		var rows = new List<IReadOnlyList<string>>(); var lines = new List<int>(); int lineNumber = 1;
		while (reader.ReadLine() is { } line)
		{
			lineNumber++; if (line.Length == 0) continue;
			AssetBinary.Require(line.Length <= 1048576, "TXT line exceeds budget.");
			string[] fields = line.Split('\t');
			AssetBinary.Require(fields.Length <= columns.Length && fields.All(f => f.Length <= 16384), $"Invalid TXT row at line {lineNumber}.");
			AssetBinary.Require(rows.Count < MaxRows && (long)(rows.Count + 1) * columns.Length <= MaxCells, "TXT cell budget exceeded.");
			int present = fields.Length; Array.Resize(ref fields, columns.Length);
			for (int i = present; i < fields.Length; i++) fields[i] = "";
			rows.Add(Array.AsReadOnly(fields)); lines.Add(lineNumber);
		}
		return new(columns, rows, lines);
	}
	public int Column(string name)
	{
		int found = -1;
		for (int i = 0; i < Columns.Count; i++) if (string.Equals(Columns[i], name, StringComparison.OrdinalIgnoreCase))
		{
			AssetBinary.Require(found < 0, $"Ambiguous TXT column: {name}."); found = i;
		}
		AssetBinary.Require(found >= 0, $"Missing TXT column: {name}."); return found;
	}
	public string Value(int row, string column) => Rows[row][Column(column)];
	public uint Number(int row, string column)
	{
		string value = Value(row, column).Trim(); if (value.Length == 0) return 0;
		// DWORD masks also appear as signed decimal values, e.g. -1 for all 32 bits.
		AssetBinary.Require(long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long number) && number >= int.MinValue && number <= uint.MaxValue,
			$"Invalid integer at TXT line {LineNumbers[row]}, column {column}.");
		return unchecked((uint)number);
	}
}

public enum MapBinSchema { LevelDefs110f, LvlTypes110f, LvlPrest110f }

// Explicit 1.10f layouts; never infer the schema or version from a file's length.
public sealed class ExcelBinTable
{
	private readonly byte[] data;
	public MapBinSchema Schema { get; }
	public int RecordSize { get; }
	public int Count { get; }
	private ExcelBinTable(byte[] data, MapBinSchema schema, int size, int count)
	{ this.data = data; Schema = schema; RecordSize = size; Count = count; }
	public static ExcelBinTable Parse(ReadOnlySpan<byte> data, MapBinSchema schema)
	{
		int size = schema switch { MapBinSchema.LevelDefs110f => 156, MapBinSchema.LvlTypes110f => 1928, MapBinSchema.LvlPrest110f => 432, _ => throw new ArgumentOutOfRangeException(nameof(schema)) };
		AssetBinary.Require(data.Length is >= 4 and <= AssetDecoders.MaxInputBytes, "BIN input exceeds size bounds.");
		uint count = AssetBinary.U32(data, 0);
		AssetBinary.Require(count <= 65536 && 4L + count * (long)size == data.Length, "BIN count/record size mismatch for selected 1.10f schema.");
		return new(data.ToArray(), schema, size, (int)count);
	}
	public ReadOnlySpan<byte> Record(int index)
	{
		if ((uint)index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
		return data.AsSpan(4 + index * RecordSize, RecordSize);
	}
	public uint Number(int row, int offset) => AssetBinary.U32(Record(row), offset);
	public string Text(int row, int offset, int length)
	{
		var field = AssetBinary.Slice(Record(row), offset, length); int end = field.IndexOf((byte)0);
		AssetBinary.Require(end >= 0, "BIN string lacks a terminator.");
		return Encoding.Latin1.GetString(field[..end]);
	}
}
