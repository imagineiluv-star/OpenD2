namespace OpenD2.Assets;

public sealed record TileCacheStats(long BudgetBytes, long RetainedBytes, int Entries, long Hits, long Misses, long Evictions);

// Retains decoded index planes only. Active scenes, temporary decode buffers and GPU textures have separate budgets.
public sealed class TileFrameCache
{
	private sealed record Entry(string Key, IndexedFrame Frame, long Charge);
	private readonly object gate = new();
	private readonly Dictionary<string, LinkedListNode<Entry>> entries = new(StringComparer.Ordinal);
	private readonly LinkedList<Entry> lru = new();
	private readonly long budget;
	private long retained, hits, misses, evictions;
	public TileFrameCache(long budgetBytes = 16777216)
	{
		if (budgetBytes < 0 || budgetBytes > 536870912) throw new ArgumentOutOfRangeException(nameof(budgetBytes)); budget = budgetBytes;
	}
	public TileCacheStats Stats { get { lock (gate) return new(budget, retained, entries.Count, hits, misses, evictions); } }
	public IndexedFrame GetFrame(Dt1Tileset tileset, int index)
	{
		ArgumentNullException.ThrowIfNull(tileset);
		if ((uint)index >= tileset.Tiles.Count) throw new ArgumentOutOfRangeException(nameof(index));
		string key = $"{tileset.ContentHash}:{AssetDecoders.Version}:indexed:{index}";
		lock (gate)
		{
			if (entries.TryGetValue(key, out var node)) { hits++; lru.Remove(node); lru.AddFirst(node); return Copy(node.Value.Frame); }
			misses++;
			var frame = tileset.DecodeTile(index); // Failed decodes are never retained. Serializes concurrent misses.
			long charge = frame.Indices.LongLength + 512L + key.Length * 2L;
			if (charge <= budget)
			{
				while (retained > budget - charge || entries.Count >= 256)
				{
					var last = lru.Last!; retained -= last.Value.Charge; entries.Remove(last.Value.Key); lru.RemoveLast(); evictions++;
				}
				entries.Add(key, lru.AddFirst(new Entry(key, Copy(frame), charge))); retained += charge;
			}
			return frame;
		}
	}
	public void Clear() { lock (gate) { entries.Clear(); lru.Clear(); retained = 0; } }
	private static IndexedFrame Copy(IndexedFrame frame) => frame with { Indices = (byte[])frame.Indices.Clone() };
}
