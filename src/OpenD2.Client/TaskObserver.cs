using Godot;

namespace OpenD2.Client;

// Single place where UI callbacks hand off a Task they cannot await. Faults that escape the
// task's own handling are logged instead of crashing the process or being silently dropped.
internal static class TaskObserver
{
	public static async void Observe(Task work, string action)
	{
		try { await work; }
		catch (Exception error) { GD.PushError($"{action} failed: {error}"); }
	}
}
