namespace OpenD2.Core;

public enum CollisionCell : byte { Unknown, Open, Blocked }

// Immutable navigation cells; one legacy DS1 tile maps to 5 x 5 of these cells.
public sealed class CollisionGrid
{
	public const int CellSize = GameSimulation.UnitsPerTile, BodyRadius = 64, MaxCells = 1048576;
	private readonly CollisionCell[] cells;
	public RegionId Region { get; }
	public GamePosition Origin { get; }
	public int Width { get; }
	public int Height { get; }
	public ReadOnlySpan<CollisionCell> Cells => cells;
	public CollisionGrid(RegionId region, int width, int height, ReadOnlySpan<CollisionCell> cells, GamePosition origin = default)
	{
		if (region.Value == 0 || width is < 1 or > 4096 || height is < 1 or > 4096 || (long)width * height > MaxCells || cells.Length != (long)width * height)
			throw new ArgumentException("Invalid collision region, dimensions or cell budget.");
		if (origin.X < -GameSimulation.PositionLimit || origin.Y < -GameSimulation.PositionLimit ||
			(long)origin.X + (long)width * CellSize > GameSimulation.PositionLimit || (long)origin.Y + (long)height * CellSize > GameSimulation.PositionLimit)
			throw new ArgumentException("Collision grid exceeds coordinate bounds.");
		foreach (var cell in cells) if (cell is not (CollisionCell.Unknown or CollisionCell.Open or CollisionCell.Blocked)) throw new ArgumentException("Invalid collision cell.");
		Region = region; Width = width; Height = height; Origin = origin; this.cells = cells.ToArray();
	}
	public CollisionCell At(int x, int y) => (uint)x < Width && (uint)y < Height ? cells[y * Width + x] : CollisionCell.Unknown;
	public bool CanOccupy(GamePosition position)
	{
		long left = (long)position.X - Origin.X - BodyRadius, top = (long)position.Y - Origin.Y - BodyRadius;
		long right = (long)position.X - Origin.X + BodyRadius - 1, bottom = (long)position.Y - Origin.Y + BodyRadius - 1;
		if (left < 0 || top < 0 || right >= (long)Width * CellSize || bottom >= (long)Height * CellSize) return false;
		for (int y = (int)(top / CellSize); y <= bottom / CellSize; y++)
			for (int x = (int)(left / CellSize); x <= right / CellSize; x++) if (At(x, y) != CollisionCell.Open) return false;
		return true;
	}
	// Integer supercover traversal also checks BOTH side cells at an exact corner.
	// Melee only: callers cannot turn this into an unbounded world ray cast.
	public bool HasMeleeLine(GamePosition from, GamePosition to)
	{
		long dx = (long)to.X - from.X, dy = (long)to.Y - from.Y;
		if (Math.Abs(dx) > GameSimulation.AttackRange || Math.Abs(dy) > GameSimulation.AttackRange || dx * dx + dy * dy > (long)GameSimulation.AttackRange * GameSimulation.AttackRange) return false;
		long fx = (long)from.X - Origin.X, fy = (long)from.Y - Origin.Y, tx = (long)to.X - Origin.X, ty = (long)to.Y - Origin.Y;
		if (fx < 0 || fy < 0 || tx < 0 || ty < 0 || fx >= (long)Width * CellSize || tx >= (long)Width * CellSize || fy >= (long)Height * CellSize || ty >= (long)Height * CellSize) return false;
		int x = (int)(fx / CellSize), y = (int)(fy / CellSize), endX = (int)(tx / CellSize), endY = (int)(ty / CellSize);
		int sx = Math.Sign(dx), sy = Math.Sign(dy); long ax = Math.Abs(dx), ay = Math.Abs(dy);
		long nx = sx > 0 ? (x + 1L) * CellSize - fx : fx - x * (long)CellSize;
		long ny = sy > 0 ? (y + 1L) * CellSize - fy : fy - y * (long)CellSize;
		while (true)
		{
			if (At(x, y) != CollisionCell.Open) return false;
			if (x == endX && y == endY) return true;
			// A negative-axis crossing at the endpoint can leave its owning cell
			// and overshoot forever toward the map edge. Stop each axis at its end
			// cell; interior corner crossings still check both neighboring cells.
			long horizontal = x == endX ? long.MaxValue : nx * ay;
			long vertical = y == endY ? long.MaxValue : ny * ax;
			if (horizontal == vertical)
			{
				if (At(x + sx, y) != CollisionCell.Open || At(x, y + sy) != CollisionCell.Open) return false;
				x += sx; y += sy; nx += CellSize; ny += CellSize;
			}
			else if (horizontal < vertical) { x += sx; nx += CellSize; }
			else { y += sy; ny += CellSize; }
		}
	}
}
