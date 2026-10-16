using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using GitExtensions.Linux.Desktop.Localization;
using GitExtensions.Linux.Scripts;

namespace GitExtensions.Linux.Desktop;

/// <summary>The original SimplePrompt input, independent of script execution.</summary>
internal sealed class SimplePromptDialog : Window
{
    private readonly TextBox _input;
    private bool _closed;
    internal bool WasAccepted { get; private set; }
    internal string UserInput { get; private set; } = "";
    internal ScriptReply Reply => WasAccepted ? new(ScriptReplyKind.Accepted, UserInput) : new(ScriptReplyKind.Cancelled);

    internal SimplePromptDialog(string? title = null, string? label = null, string? defaultValue = null, TranslationCatalog? translations = null)
    {
        translations ??= TranslationCatalog.LoadDefault();
        Name = "SimplePromptDialog"; Title = string.IsNullOrWhiteSpace(title) ? "User input" : title;
        Width = 334; Height = 76;
        _input = ScriptInputDialogControls.Text("txtUserInput", defaultValue); _input.Height = _input.MinHeight = 23; _input.TabIndex = 1;
        TextBlock caption = new() { Name = "labelInput", Text = string.IsNullOrWhiteSpace(label) ? "Please specify your input:" : label + ":", Margin = new Thickness(-2, 0, 0, 0) };
        Button accept = ScriptInputDialogControls.Accept("btnOk", translations, "SimplePrompt", 75, 23);
        accept.Width = 75; accept.TabIndex = 0; accept.HorizontalAlignment = HorizontalAlignment.Right; accept.VerticalAlignment = VerticalAlignment.Bottom;
        accept.Click += (_, _) => { if (!_closed) { UserInput = _input.Text ?? ""; WasAccepted = true; Close(); } };
        Grid body = new() { RowDefinitions = new RowDefinitions("16,23,6,*"), Margin = new Thickness(12, 5, 12, 3) };
        body.Children.Add(caption); Grid.SetRow(_input, 1); body.Children.Add(_input); Grid.SetRow(accept, 3); body.Children.Add(accept); Content = body;
        ScriptInputDialogControls.Configure(this, _input, () => !_closed, selectAll: true);
        AddHandler(KeyDownEvent, (_, args) =>
        {
            if (args.Key != Key.Escape || args.Handled || !_input.IsKeyboardFocusWithin) { return; }
            args.Handled = true;
            if (_input.SelectionStart != _input.SelectionEnd) { _input.SelectionEnd = _input.SelectionStart; }
            else { Close(); }
        }, RoutingStrategies.Tunnel);
        Closed += (_, _) => _closed = true;
    }
}
