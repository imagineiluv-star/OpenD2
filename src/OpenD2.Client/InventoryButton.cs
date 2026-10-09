using Godot;

namespace OpenD2.Client;

// Godot drag payloads propose commands; the simulation owns every transfer.
public partial class InventoryButton : Button
{
	public Func<Variant>? BeginDrag { get; set; }
	public Func<Vector2, Variant, bool>? CanDrop { get; set; }
	public Action<Vector2, Variant>? Drop { get; set; }
	public Action? CancelMove { get; set; }
	public override Variant _GetDragData(Vector2 atPosition) => BeginDrag?.Invoke() ?? default;
	public override bool _CanDropData(Vector2 atPosition, Variant data) => CanDrop?.Invoke(atPosition, data) == true;
	public override void _DropData(Vector2 atPosition, Variant data) => Drop?.Invoke(atPosition, data);
	public override void _GuiInput(InputEvent input)
	{
		if (input is InputEventKey { Pressed: true, Keycode: Key.Escape }) { CancelMove?.Invoke(); AcceptEvent(); }
	}
}
