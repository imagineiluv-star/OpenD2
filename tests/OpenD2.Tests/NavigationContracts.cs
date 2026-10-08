using OpenD2.Core;

internal static class NavigationContracts
{
	private static void Check(bool value) { if (!value) throw new Exception("Navigation assertion failed."); }
	private static CollisionGrid Maze()
	{
		var cells = new CollisionCell[49];
		for (int y = 0; y < 7; y++) for (int x = 0; x < 7; x++) cells[y * 7 + x] = x == 0 || y == 0 || x == 6 || y == 6 || (x == 3 && y < 5) ? CollisionCell.Blocked : CollisionCell.Open;
		return new(new(1), 7, 7, cells);
	}
	public static void Run(Action<string, Action> test)
	{
		test("bounded pathfinder routes around a wall without cutting corners", () =>
		{
			var grid = Maze(); var from = new GamePosition(384, 640); var to = new GamePosition(1408, 640);
			var path = GridPathfinder.Find(grid, from, to); Check(path.Status == PathStatus.Found && path.Points.Any(p => p.Y == 1408));
			var last = from; foreach (var next in path.Points) { Check(GridPathfinder.HasWalkLine(grid, last, next)); last = next; }
			Check(last == to && path.Points.SequenceEqual(GridPathfinder.Find(grid, from, to).Points));
		});
		test("pathfinder rejects unknown terrain and diagonal corner gaps", () =>
		{
			CollisionCell[] cells = [CollisionCell.Open, CollisionCell.Unknown, CollisionCell.Blocked, CollisionCell.Open];
			var grid = new CollisionGrid(new(1), 2, 2, cells);
			Check(GridPathfinder.Find(grid, new(128, 128), new(384, 384)).Status == PathStatus.Unreachable);
			Check(!GridPathfinder.HasWalkLine(grid, new(128, 128), new(384, 384)));
			Check(GridPathfinder.Find(grid, new(0, 0), new(128, 128)).Status == PathStatus.Unreachable);
		});
		test("path search budget returns no partial route and same-cell routes are empty", () =>
		{
			var grid = Maze(); var from = new GamePosition(384, 640);
			var limited = GridPathfinder.Find(grid, from, new(1408, 640), 1);
			Check(limited.Status == PathStatus.BudgetExceeded && limited.Expanded == 1 && limited.Points.Count == 0);
			var same = GridPathfinder.Find(grid, from, from); Check(same.Status == PathStatus.Found && same.Points.Count == 0 && same.Expanded == 0);
		});
		test("opt-in monster navigation reaches a target across a wall and restores deterministically", () =>
		{
			var grid = Maze();
			var world = new WorldDefinition([new("Maze", grid)], [], new(new(10), new(1), new(384, 1280), "Guide"), [new(2)], "Test", navigateWalls: true);
			EntityState[] actors = [new(new(1), new(1), new(1408, 640)), new(new(2), new(1), new(384, 640), Kind: EntityKind.Monster)];
			var simulation = new GameSimulation(1, actors, world: world);
			for (int i = 0; i < 30; i++) simulation.Step();
			var resumed = GameSimulation.Restore(simulation.CaptureSnapshot());
			for (int i = 0; i < 170; i++) { simulation.Step(); resumed.Step(); Check(simulation.ComputeStateHash() == resumed.ComputeStateHash()); }
			Check(simulation.GetEntity(new(1)).Health < 100);
		});
		test("navigation mode is part of content identity without changing default world rules", () =>
		{
			var grid = Maze();
			WorldDefinition World(bool enabled) => new([new("Maze", grid)], [], new(new(10), new(1), new(384, 1280), "Guide"), [new(2)], "Test", enabled);
			Check(World(false).ContentHash != World(true).ContentHash);
			Check(!World(false).NavigateWalls && World(true).NavigateWalls);
		});
	}
}
