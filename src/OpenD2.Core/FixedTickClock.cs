namespace OpenD2.Core;

// Owner-thread scheduler. Wall time never enters the simulation's game rules.
public sealed class FixedTickClock
{
	public const int TicksPerSecond = 25, MaxStepsPerFrame = 5;
	public static readonly TimeSpan TickDuration = TimeSpan.FromMilliseconds(40);
	public static readonly TimeSpan MaxFrameTime = TimeSpan.FromMilliseconds(250);
	private long pending;
	private bool advancing;
	public TimeSpan DroppedTime { get; private set; }
	public bool IsFaulted { get; private set; }
	public double Alpha => Math.Clamp((double)pending / TickDuration.Ticks, 0, 1);
	public int Advance(TimeSpan elapsed, Action step)
	{
		ArgumentNullException.ThrowIfNull(step);
		if (elapsed.Ticks < 0) throw new ArgumentOutOfRangeException(nameof(elapsed));
		if (advancing || IsFaulted) throw new InvalidOperationException("Clock is advancing or faulted; reset a failed clock before reuse.");
		advancing = true;
		try
		{
			long accepted = Math.Min(elapsed.Ticks, MaxFrameTime.Ticks);
			AddDropped(elapsed.Ticks - accepted); pending += accepted; int count = 0;
			while (pending >= TickDuration.Ticks && count < MaxStepsPerFrame)
			{
				step(); pending -= TickDuration.Ticks; count++;
			}
			long excess = pending - pending % TickDuration.Ticks;
			AddDropped(excess); pending -= excess; return count;
		}
		catch { IsFaulted = true; throw; }
		finally { advancing = false; }
	}
	private void AddDropped(long ticks) => DroppedTime = TimeSpan.FromTicks(ticks > long.MaxValue - DroppedTime.Ticks ? long.MaxValue : DroppedTime.Ticks + ticks);
	public void Reset()
	{
		if (advancing) throw new InvalidOperationException("Cannot reset an advancing clock.");
		pending = 0; DroppedTime = TimeSpan.Zero; IsFaulted = false;
	}
}
