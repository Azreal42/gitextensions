using Avalonia.Controls;
using Avalonia.Input;

namespace GitExtensions.Linux.Desktop.Controls;

// These buttons represent rows of the revision surface. Let Space reach
// TextInput for quick search without Button's Space-up activation.
internal sealed class RevisionSearchButton : Button
{
    protected override Type StyleKeyOverride => typeof(Button);
    protected override void OnKeyDown(KeyEventArgs args)
    { if (args.Key != Key.Space) { base.OnKeyDown(args); } }
    protected override void OnKeyUp(KeyEventArgs args)
    { if (args.Key != Key.Space) { base.OnKeyUp(args); } }
}
