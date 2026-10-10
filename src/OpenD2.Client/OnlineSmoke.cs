using Godot;

namespace OpenD2.Client;

public partial class OnlineSmoke : Node
{
    public override async void _Ready()
    {
        try
        {
            var args = OS.GetCmdlineUserArgs();
            string Value(string prefix) => args.Single(a => a.StartsWith(prefix, StringComparison.Ordinal))[prefix.Length..];
            GetWindow().Size = new(1100, 900);
            var panel = new OnlinePanel(); AddChild(panel);
            panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            await panel.CheckOnline(Value("--server="), Value("--role="), Value("--run="), Value("--evidence="));
            GetTree().Quit();
        }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
    }
}
