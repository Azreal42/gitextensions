using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace GitExtensions.Linux.Desktop;

public sealed partial class SubmoduleConflictDialog
{
    private readonly TextBox _base = RevisionField("tbBase", 7);
    private readonly TextBox _local = RevisionField("tbLocal", 8);
    private readonly TextBox _remote = RevisionField("tbRemote", 9);
    private readonly TextBox _current = RevisionField("tbCurrent", 10);
    private readonly TextBox _path = new()
    {
        Name = "lbSubmodule", IsReadOnly = true, AcceptsReturn = false, AcceptsTab = false,
        BorderThickness = new Thickness(0), Padding = new Thickness(0), Background = Brushes.Transparent,
        Height = 30, MinHeight = 0, TabIndex = 14, TextWrapping = TextWrapping.NoWrap
    };
    private readonly Button _open, _checkout, _stage, _refresh;

    private static TextBox RevisionField(string name, int tabIndex) => new()
    { Name = name, IsReadOnly = true, Width = 438, Height = 27, MinHeight = 0, TabIndex = tabIndex };

    private string Original(string name, string text) => Translations.Translate("FormMergeSubmodule", name, "Text", text);

    private Button OriginalButton(string name, string caption, Func<Task> action, string? icon = null)
    {
        Button button = Action(name, caption, action);
        string translated = Original(name, caption);
        button.Content = icon is null ? translated : WorktreeDialogControls.ButtonContent(translated, icon);
        button.Height = 31; button.MinHeight = 0; button.MinWidth = 0; button.Padding = new Thickness(4, 0);
        return button;
    }

    private void BuildOriginalLayout()
    {
        if (Status.Parent is Border border) { border.Child = null; Root.Children.Remove(border); }
        Canvas form = new() { Name = "SubmoduleConflictForm", Width = 595, Height = 254, ClipToBounds = true };
        Caption("label2", "There is a conflict on the submodule:", 46, 11, 252);
        Place(_path, 308, 11, 278, 30);
        Caption("label1", "Base:", 15, 52, 88); Place(_base, 106, 49, 438, 27);
        Caption("label3", "Local:", 15, 86, 88); Place(_local, 106, 82, 438, 27);
        Caption("label4", "Remote:", 15, 122, 88); Place(_remote, 106, 119, 438, 27);
        Caption("label5", "Current:", 15, 159, 88); Place(_current, 106, 155, 438, 27);
        Place(_refresh, 552, 152, 34, 31); Place(_open, 19, 212, 176, 31);
        Place(_checkout, 203, 212, 181, 31); Place(_stage, 392, 212, 190, 31);
        Status.FontSize = Math.Max(1, DesktopFonts.For(DesktopFontRole.Application).SizeInDips - 1); Status.TextTrimming = TextTrimming.CharacterEllipsis; Status.TextWrapping = TextWrapping.NoWrap;
        Place(Status, 15, 187, 571, 20); Status.IsVisible = false;
        Status.PropertyChanged += (_, change) =>
        {
            if (change.Property == TextBlock.TextProperty)
            { Status.IsVisible = LastError is not null; ToolTip.SetTip(Status, LastError); }
        };
        Root.Children.Add(form);
        void Place(Control control, double x, double y, double width, double height)
        { control.Width = width; control.Height = height; Canvas.SetLeft(control, x); Canvas.SetTop(control, y); form.Children.Add(control); }
        void Caption(string name, string text, double x, double y, double width)
        {
            string caption = Original(name, text);
            TextBlock label = new() { Name = name, Text = caption, TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(label, caption); Place(label, x, y, width, 20);
        }
    }
}
