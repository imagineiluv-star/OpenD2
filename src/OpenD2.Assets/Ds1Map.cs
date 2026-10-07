using System.Text;

namespace OpenD2.Assets;

public enum MapLayerKind { Floor, Wall, Shadow, Tag }
public readonly record struct MapCell(uint Raw, uint RawOrientation, int Orientation)
{
	public bool Present => (Raw & 255) != 0;
	public bool Hidden => (Raw & 0x80000000) != 0;
	public TileKey Key => new((int)((Raw >> 20) & 63), (int)((Raw >> 8) & 63), Orientation);
}
public sealed record MapLayer(MapLayerKind Kind, int Index, IReadOnlyList<MapCell> Cells);
public sealed record MapObject(uint Type, uint Id, uint X, uint Y, uint Flags);
public sealed record MapGroup(uint X, uint Y, uint Width, uint Height, uint Unknown);
public readonly record struct MapPathPoint(uint X, uint Y, uint Action);
public sealed record MapPath(uint X, uint Y, IReadOnlyList<MapPathPoint> Points);

public sealed class Ds1Map
{
	public const int MaxCells = 65536;
	public int Version { get; }
	public int Width { get; }
	public int Height { get; }
	public int Act { get; }
	public uint SubstitutionType { get; }
	public IReadOnlyList<string> FileReferences { get; }
	public IReadOnlyList<MapLayer> Layers { get; }
	public IReadOnlyList<MapObject> Objects { get; }
	public IReadOnlyList<MapGroup> Groups { get; }
	public IReadOnlyList<MapPath> Paths { get; }
	private Ds1Map(int version, int width, int height, int act, uint substitution, string[] files, MapLayer[] layers, MapObject[] objects, MapGroup[] groups, MapPath[] paths)
	{
		Version = version; Width = width; Height = height; Act = act; SubstitutionType = substitution;
		FileReferences = Array.AsReadOnly(files); Layers = Array.AsReadOnly(layers); Objects = Array.AsReadOnly(objects);
		Groups = Array.AsReadOnly(groups); Paths = Array.AsReadOnly(paths);
	}
	public static Ds1Map Parse(ReadOnlySpan<byte> data)
	{
		AssetBinary.Require(data.Length is >= 12 and <= AssetDecoders.MaxInputBytes, "DS1 input exceeds size bounds.");
		var reader = new MapReader(data);
		int version = reader.Count(18); AssetBinary.Require(version >= 1, "Unsupported DS1 version (expected 1..18).");
		int width = reader.Count(1023) + 1, height = reader.Count(1023) + 1;
		int cells = width * height; AssetBinary.Require(cells <= MaxCells, "DS1 cell budget exceeded.");
		int act = version >= 8 ? reader.Count(4) + 1 : 1;
		uint substitution = version >= 10 ? reader.UInt() : 0;
		AssetBinary.Require(substitution <= 2, "Unsupported DS1 substitution type.");
		var files = new string[version >= 3 ? reader.Count(256) : 0];
		for (int i = 0; i < files.Length; i++) files[i] = reader.String();
		if (version is >= 9 and <= 13) reader.Skip(8);
		int walls = version >= 4 ? reader.Count(4) : 1;
		int floors = version >= 16 ? reader.Count(2) : 1;
		bool tags = version < 4 || substitution is 1 or 2;
		int streams = walls * 2 + floors + 1 + (tags ? 1 : 0);
		reader.RequireBytes((long)streams * cells * 4); // Before any layer arrays are allocated.
		var layers = new List<MapLayer>();
		if (version < 4)
		{
			uint[] wall = ReadPlane(ref reader, cells), floor = ReadPlane(ref reader, cells), direction = ReadPlane(ref reader, cells);
			layers.Add(Layer(MapLayerKind.Wall, 0, wall, direction, version));
			layers.Add(Layer(MapLayerKind.Floor, 0, floor, null, version));
			layers.Add(Layer(MapLayerKind.Tag, 0, ReadPlane(ref reader, cells), null, version));
			layers.Add(Layer(MapLayerKind.Shadow, 0, ReadPlane(ref reader, cells), null, version));
		}
		else
		{
			for (int i = 0; i < walls; i++)
			{
				uint[] values = ReadPlane(ref reader, cells), orientations = ReadPlane(ref reader, cells);
				layers.Add(Layer(MapLayerKind.Wall, i, values, orientations, version));
			}
			for (int i = 0; i < floors; i++) layers.Add(Layer(MapLayerKind.Floor, i, ReadPlane(ref reader, cells), null, version));
			layers.Add(Layer(MapLayerKind.Shadow, 0, ReadPlane(ref reader, cells), null, version));
			if (tags) layers.Add(Layer(MapLayerKind.Tag, 0, ReadPlane(ref reader, cells), null, version));
		}
		var objects = new MapObject[version >= 2 ? reader.Count(16384) : 0];
		reader.RequireBytes((long)objects.Length * (version > 5 ? 20 : 16));
		for (int i = 0; i < objects.Length; i++) objects[i] = new(reader.UInt(), reader.UInt(), reader.UInt(), reader.UInt(), version > 5 ? reader.UInt() : 0);
		MapGroup[] groups = [];
		if (version >= 12 && tags)
		{
			if (version >= 18) reader.Skip(4);
			groups = new MapGroup[reader.Count(16384)]; reader.RequireBytes((long)groups.Length * (version >= 13 ? 20 : 16));
			for (int i = 0; i < groups.Length; i++) groups[i] = new(reader.UInt(), reader.UInt(), reader.UInt(), reader.UInt(), version >= 13 ? reader.UInt() : 0);
		}
		var paths = new MapPath[version >= 14 ? reader.Count(16384) : 0];
		int points = 0;
		for (int i = 0; i < paths.Length; i++)
		{
			int count = reader.Count(65536); points += count;
			AssetBinary.Require(points <= 65536, "DS1 path point budget exceeded.");
			uint x = reader.UInt(), y = reader.UInt(); reader.RequireBytes(count * (version >= 15 ? 12L : 8L));
			var path = new MapPathPoint[count];
			for (int p = 0; p < count; p++) path[p] = new(reader.UInt(), reader.UInt(), version >= 15 ? reader.UInt() : 1);
			// Keep unbound and ambiguous paths. Do not attach them to an arbitrary object.
			paths[i] = new(x, y, Array.AsReadOnly(path));
		}
		AssetBinary.Require(reader.Remaining == 0, "DS1 trailing data is not supported.");
		return new(version, width, height, act, substitution, files, layers.ToArray(), objects, groups, paths);
	}
	private static uint[] ReadPlane(ref MapReader reader, int count)
	{
		var values = new uint[count]; for (int i = 0; i < count; i++) values[i] = reader.UInt(); return values;
	}
	private static MapLayer Layer(MapLayerKind kind, int index, uint[] values, uint[]? directions, int version)
	{
		ReadOnlySpan<byte> oldDirections = [0, 1, 2, 1, 2, 3, 3, 5, 5, 6, 6, 7, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 20];
		var cells = new MapCell[values.Length];
		for (int i = 0; i < cells.Length; i++)
		{
			uint rawDirection = directions?[i] ?? 0;
			int direction = kind == MapLayerKind.Shadow ? 13 : (int)(rawDirection & 255);
			if (kind == MapLayerKind.Wall && version < 7)
			{
				AssetBinary.Require(direction < oldDirections.Length, "Invalid legacy DS1 wall orientation."); direction = oldDirections[direction];
			}
			cells[i] = new(values[i], rawDirection, direction);
		}
		return new(kind, index, Array.AsReadOnly(cells));
	}
	private ref struct MapReader(ReadOnlySpan<byte> data)
	{
		private readonly ReadOnlySpan<byte> data = data;
		private int position;
		public int Remaining => data.Length - position;
		public void RequireBytes(long count) => AssetBinary.Slice(data, position, count);
		public void Skip(int count) { RequireBytes(count); position += count; }
		public uint UInt() { uint value = AssetBinary.U32(data, position); position += 4; return value; }
		public int Count(int max) { uint value = UInt(); AssetBinary.Require(value <= max, "DS1 value exceeds supported range."); return (int)value; }
		public string String()
		{
			var tail = data.Slice(position, Math.Min(Remaining, 1025)); int length = tail.IndexOf((byte)0);
			AssetBinary.Require(length >= 0, "DS1 reference missing terminator or exceeds 1024 bytes.");
			string value = Encoding.Latin1.GetString(tail[..length]); position += length + 1; return value;
		}
	}
}
