using OpenD2.Core;

namespace OpenD2.Online;

// Versioned, built-in synthetic content. Requests select this ID/hash, never a path
// or client-supplied collision/entities. Owned MPQ content is a separate milestone.
public static class CooperativeWorld
{
    public static WorldDefinition Definition { get; } = Build();
    // Which kind each Field monster is. Not part of the content hash: the simulation does not know kinds.
    public static MonsterRoster Roster { get; } = new([(100, "preview-grunt"), (101, "preview-brute")]);
    // Include seed, actors, spawn positions and initial items as well as map content.
    public static WorldContent Content { get; } = new("cooperative-demo-v2", Create(4).ComputeStateHash());

    private static WorldDefinition Build()
    {
        CollisionGrid Grid(uint region)
        {
            var cells = Enumerable.Repeat(CollisionCell.Open, Arena.Width * Arena.Height).ToArray();
            for (int y = 0; y < Arena.Height; y++) for (int x = 0; x < Arena.Width; x++)
                if (x == 0 || y == 0 || x == Arena.Width - 1 || y == Arena.Height - 1)
                    cells[y * Arena.Width + x] = CollisionCell.Blocked;
            return new(new(region), Arena.Width, Arena.Height, cells);
        }
        return new([new("Camp", Grid(1)), new("Field", Grid(2))],
            [new(new(200), new(1), new(1024, 768), new(2), new(768, 768)),
             new(new(201), new(2), new(1024, 768), new(1), new(768, 768))],
            new(new(300), new(1), new(768, 512), "Scout"), [new(100), new(101)],
            "Clear the field and return", navigateWalls: true, playerLimit: 4, personalQuests: true);
    }

    public static GameSimulation Create(int players)
    {
        if (players is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(players));
        return new(42, Enumerable.Range(1, players)
            .Select(i => new EntityState(new((uint)i), new(1), new(768, 768 + (i - 1) * 512)))
            .Concat([new EntityState(new(100), new(2), new(3584, 2304), Kind: EntityKind.Monster, Health: 60, MaxHealth: 60),
                     new EntityState(new(101), new(2), new(4864, 3584), Kind: EntityKind.Monster, Health: 60, MaxHealth: 60)]),
            world: Definition);
    }

    public static WorldDefinition Resolve(WorldContent content)
    {
        if (content != Content) throw new InvalidDataException("World content ID/hash does not match this server build.");
        return Definition;
    }
}
