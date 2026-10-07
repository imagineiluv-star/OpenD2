namespace OpenD2.Assets;

public sealed record MapTileset(string Name, Dt1Tileset Tileset);
public sealed record MapImage(string Source, int TileIndex, Dt1Tile Tile, IndexedFrame Frame);
public readonly record struct MapPlacement(int X, int Y, int Layer, int Image, TileKey Key, int PixelX, int PixelY);
public readonly record struct MapCollision(byte Flags, bool Known)
{
	public bool BlocksWalk => !Known || (Flags & 9) != 0;
}

// A bounded, deterministic inspection scene. Random map generation and dynamic collision belong to the game simulation.
public sealed class MapScene
{
	private readonly byte[] collision;
	private readonly bool[] known;
	public Ds1Map Map { get; }
	public IReadOnlyList<MapImage> Images { get; }
	public IReadOnlyList<MapPlacement> Placements { get; }
	public int MissingTiles { get; }
	public int DuplicateKeys { get; }
	private MapScene(Ds1Map map, MapImage[] images, MapPlacement[] placements, byte[] collision, bool[] known, int missing, int duplicates)
	{
		Map = map; Images = Array.AsReadOnly(images); Placements = Array.AsReadOnly(placements);
		this.collision = collision; this.known = known; MissingTiles = missing; DuplicateKeys = duplicates;
	}
	public static MapScene Build(Ds1Map map, IReadOnlyList<MapTileset> tilesets)
	{
		AssetBinary.Require(tilesets.Count <= 32 && tilesets.Sum(s => (long)s.Tileset.Tiles.Count) <= 8192, "Map tileset budget exceeded.");
		var lookup = new Dictionary<TileKey, (int Set, int Tile)>(); int duplicates = 0;
		for (int s = 0; s < tilesets.Count; s++)
			for (int t = 0; t < tilesets[s].Tileset.Tiles.Count; t++)
				if (!lookup.TryAdd(tilesets[s].Tileset.Tiles[t].Key, (s, t))) duplicates++;
		var images = new List<MapImage>(); var decoded = new Dictionary<(int Set, int Tile), int>();
		var placements = new List<MapPlacement>(); long pixels = 0;
		var collision = new byte[map.Width * map.Height * 25]; var missingCollision = new bool[map.Width * map.Height];
		var hasFloor = new bool[missingCollision.Length]; int missing = 0;
		for (int l = 0; l < map.Layers.Count; l++)
		{
			var layer = map.Layers[l]; if (layer.Kind == MapLayerKind.Tag) continue;
			for (int c = 0; c < layer.Cells.Count; c++)
			{
				var cell = layer.Cells[c]; if (!cell.Present || cell.Hidden) continue;
				int image = -1, x = c % map.Width, y = c / map.Width;
				int px = (x - y) * 80 - 80, py = (x + y) * 40;
				if (lookup.TryGetValue(cell.Key, out var reference))
				{
					if (!decoded.TryGetValue(reference, out image))
					{
						var source = tilesets[reference.Set]; var tile = source.Tileset.Tiles[reference.Tile];
						pixels += (long)tile.PixelWidth * tile.PixelHeight;
						AssetBinary.Require(pixels <= Dt1Tileset.MaxPixels, "Map decoded pixel budget exceeded.");
						image = images.Count; decoded.Add(reference, image);
						images.Add(new(source.Name, reference.Tile, tile, source.Tileset.DecodeTile(reference.Tile)));
					}
					var asset = images[image]; px += asset.Frame.Left;
					py += asset.Frame.Top + (cell.Orientation is 0 or 15 ? 0 : 80) - asset.Tile.RoofHeight;
					if (layer.Kind == MapLayerKind.Floor) hasFloor[c] = true;
					if (layer.Kind != MapLayerKind.Shadow && cell.Orientation != 15)
						for (int sy = 0; sy < 5; sy++) for (int sx = 0; sx < 5; sx++)
							collision[c * 25 + sy * 5 + sx] |= asset.Tile.CollisionAt(sx, sy);
				}
				else
				{
					missing++;
					if (layer.Kind != MapLayerKind.Shadow && cell.Orientation != 15) missingCollision[c] = true;
				}
				placements.Add(new(x, y, l, image, cell.Key, px, py));
			}
		}
		var known = new bool[missingCollision.Length];
		for (int i = 0; i < known.Length; i++) known[i] = hasFloor[i] && !missingCollision[i];
		// Inspection draw order: floors, lower walls, shadows, depth-sorted upper walls, roofs.
		int Pass(MapPlacement p) => map.Layers[p.Layer].Kind == MapLayerKind.Floor ? 0 : p.Key.Orientation >= 16 ? 1 : map.Layers[p.Layer].Kind == MapLayerKind.Shadow ? 2 : p.Key.Orientation == 15 ? 4 : 3;
		var ordered = placements.OrderBy(Pass).ThenBy(p => p.X + p.Y).ThenBy(p => p.X).ThenBy(p => p.Layer).ToArray();
		return new(map, images.ToArray(), ordered, collision, known, missing, duplicates);
	}
	public MapCollision CollisionAt(int subtileX, int subtileY)
	{
		if ((uint)subtileX >= Map.Width * 5 || (uint)subtileY >= Map.Height * 5) throw new ArgumentOutOfRangeException(nameof(subtileX));
		int cell = (subtileY / 5) * Map.Width + subtileX / 5;
		return new(collision[cell * 25 + (subtileY % 5) * 5 + subtileX % 5], known[cell]);
	}
}
