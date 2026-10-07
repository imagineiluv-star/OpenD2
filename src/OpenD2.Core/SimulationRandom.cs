namespace OpenD2.Core;

// Explicit xorshift32 state, matching OpenD2's uint32 Seed_Next transition.
// This is a versioned simulation rule, not a claim of original Diablo RNG compatibility.
public struct SimulationRandom
{
	public uint State { get; private set; }
	public SimulationRandom(uint seed)
	{
		if (seed == 0) throw new ArgumentOutOfRangeException(nameof(seed), "Random state must be nonzero."); State = seed;
	}
	public uint NextUInt32()
	{
		if (State == 0) throw new InvalidOperationException("Initialize random state before use.");
		uint next = State; next ^= next << 13; next ^= next >> 17; next ^= next << 5;
		State = next; return next;
	}
	public int NextInt(int exclusiveMax)
	{
		if (exclusiveMax <= 0) throw new ArgumentOutOfRangeException(nameof(exclusiveMax));
		// xorshift32 emits 1..uint.MaxValue. Subtract one before rejection sampling.
		uint bound = (uint)exclusiveMax, limit = uint.MaxValue - uint.MaxValue % bound, value;
		do { value = NextUInt32() - 1; } while (value >= limit);
		return (int)(value % bound);
	}
}
