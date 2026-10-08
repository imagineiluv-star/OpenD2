namespace OpenD2.Core;

public enum PathStatus { Found, Unreachable, BudgetExceeded }
public sealed record GridPath(PathStatus Status, IReadOnlyList<GamePosition> Points, int Expanded);

public static class GridPathfinder
{
	public const int MaxNodes = 4096;
	private readonly record struct Node(int Cost, int Parent);
	private static readonly (int X, int Y)[] Neighbors = [(0, -1), (-1, 0), (1, 0), (0, 1), (-1, -1), (1, -1), (-1, 1), (1, 1)];
	public static GridPath Find(CollisionGrid grid, GamePosition from, GamePosition to, int maxExpanded = MaxNodes)
	{
		ArgumentNullException.ThrowIfNull(grid);
		if (maxExpanded is < 1 or > MaxNodes) throw new ArgumentOutOfRangeException(nameof(maxExpanded));
		if (!grid.CanOccupy(from) || !grid.CanOccupy(to)) return new(PathStatus.Unreachable, [], 0);
		int Cell(GamePosition p) => (p.Y - grid.Origin.Y) / 256 * grid.Width + (p.X - grid.Origin.X) / 256;
		GamePosition Center(int cell) => new(grid.Origin.X + cell % grid.Width * 256 + 128, grid.Origin.Y + cell / grid.Width * 256 + 128);
		int start = Cell(from), goal = Cell(to), gx = goal % grid.Width, gy = goal / grid.Width;
		int Heuristic(int cell) { int dx = Math.Abs(cell % grid.Width - gx), dy = Math.Abs(cell / grid.Width - gy); return 10 * Math.Max(dx, dy) + 4 * Math.Min(dx, dy); }
		var nodes = new Dictionary<int, Node> { [start] = new(0, -1) }; var closed = new HashSet<int>();
		var pending = new PriorityQueue<(int Cell, int Cost), (int F, int H, int Cell)>(); pending.Enqueue((start, 0), (Heuristic(start), Heuristic(start), start));
		int expanded = 0;
		while (pending.TryDequeue(out var current, out _))
		{
			if (closed.Contains(current.Cell) || nodes[current.Cell].Cost != current.Cost) continue;
			if (current.Cell == goal)
			{
				var route = new List<GamePosition>(); int at = goal;
				while (at != start) { route.Add(Center(at)); at = nodes[at].Parent; }
				route.Reverse(); return new(PathStatus.Found, route.AsReadOnly(), expanded);
			}
			if (expanded >= maxExpanded) return new(PathStatus.BudgetExceeded, [], expanded);
			expanded++; closed.Add(current.Cell); int x = current.Cell % grid.Width, y = current.Cell / grid.Width;
			foreach (var step in Neighbors)
			{
				int nx = x + step.X, ny = y + step.Y;
				if (grid.At(nx, ny) != CollisionCell.Open || (step.X != 0 && step.Y != 0 && (grid.At(nx, y) != CollisionCell.Open || grid.At(x, ny) != CollisionCell.Open))) continue;
				int next = ny * grid.Width + nx, cost = current.Cost + (step.X == 0 || step.Y == 0 ? 10 : 14);
				if (closed.Contains(next) || (nodes.TryGetValue(next, out var old) && old.Cost <= cost)) continue;
				if (!nodes.ContainsKey(next) && nodes.Count >= MaxNodes) return new(PathStatus.BudgetExceeded, [], expanded);
				nodes[next] = new(cost, current.Cell); int h = Heuristic(next); pending.Enqueue((next, cost), (cost + h, h, next));
			}
		}
		return new(PathStatus.Unreachable, [], expanded);
	}
	public static bool HasWalkLine(CollisionGrid grid, GamePosition from, GamePosition to)
	{
		long dx = (long)to.X - from.X, dy = (long)to.Y - from.Y;
		// This helper is for local AI perception, not an unbounded world ray.
		if (Math.Abs(dx) > GameSimulation.AggroRange || Math.Abs(dy) > GameSimulation.AggroRange) return false;
		int steps = (int)((Math.Max(Math.Abs(dx), Math.Abs(dy)) + 31) / 32);
		for (int i = 0; i <= steps; i++)
		{
			var point = steps == 0 ? from : new GamePosition(from.X + (int)(dx * i / steps), from.Y + (int)(dy * i / steps));
			if (!grid.CanOccupy(point)) return false;
		}
		return true;
	}
}
