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
            GetWindow().Size = new(1100, 1100);
            GetWindow().ContentScaleSize = new(1100, 1100);
            GetWindow().Position = new(Value("--role=") == "host" ? 0 : 1150, 0);
            GetWindow().Title = "OpenD2 online validation — " + Value("--role=");
            var panel = new OnlinePanel(); AddChild(panel);
            panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            await panel.CheckOnline(Value("--server="), Value("--role="), Value("--run="), Value("--evidence="), args.SingleOrDefault(a => a.StartsWith("--ca=", StringComparison.Ordinal))?[5..], args.SingleOrDefault(a => a.StartsWith("--mode=", StringComparison.Ordinal))?[7..] ?? "realm");
            GetTree().Quit();
        }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
    }
}
