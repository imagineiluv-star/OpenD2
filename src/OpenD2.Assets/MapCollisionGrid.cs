using OpenD2.Core;

namespace OpenD2.Assets;

public static class MapCollisionGrid
{
	// Retain M1's fail-closed policy: missing floor/tile data never becomes walkable.
	public static CollisionGrid ToCollisionGrid(this MapScene scene, RegionId region, GamePosition origin = default)
	{
		ArgumentNullException.ThrowIfNull(scene);
		int width = checked(scene.Map.Width * 5), height = checked(scene.Map.Height * 5);
		if ((long)width * height > CollisionGrid.MaxCells) throw new InvalidDataException("Map collision grid exceeds simulation budget.");
		var cells = new CollisionCell[width * height];
		for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
		{
			var cell = scene.CollisionAt(x, y);
			cells[y * width + x] = !cell.Known ? CollisionCell.Unknown : cell.BlocksWalk ? CollisionCell.Blocked : CollisionCell.Open;
		}
		return new(region, width, height, cells, origin);
	}
}
