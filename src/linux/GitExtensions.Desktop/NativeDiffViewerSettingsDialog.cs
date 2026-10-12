using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using GitExtensions.Linux.Desktop.Localization;

namespace GitExtensions.Linux.Desktop;

/// <summary>The ported General and Diff coloring sections of the original DiffViewerSettingsPage.</summary>
public sealed class NativeDiffViewerSettingsDialog : Window
{
    private const string Category = "DiffViewerSettingsPage";
    private readonly DiffViewerPreferencesStore _store;
    private readonly TranslationCatalog _translations = TranslationCatalog.LoadDefault();
    private readonly CheckBox _whitespace, _nonPrinting, _wholeFile, _appearance, _context, _syntax, _gitColoring, _reverse;
    private readonly TextBlock _error = new() { Name = "DiffViewerSettingsError", TextWrapping = Avalonia.Media.TextWrapping.Wrap, IsVisible = false };

    public NativeDiffViewerSettingsDialog(DiffViewerPreferencesStore? store = null)
    {
        _store = store ?? new(); DiffViewerPreferences state = _store.Load();
        Name = "NativeDiffViewerSettingsDialog"; Title = T("$this", "Diff viewer");
        Width = 640; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _whitespace = Choice("chkRememberIgnoreWhiteSpacePreference", "Remember the 'Ignore whitespaces' preference", state.RememberWhitespace);
        _nonPrinting = Choice("chkRememberShowNonPrintingCharsPreference", "Remember the 'Show nonprinting characters' preference", state.RememberNonPrintingChars);
        _wholeFile = Choice("chkRememberShowEntireFilePreference", "Remember the 'Show entire file' preference", state.RememberWholeFile);
        _appearance = Choice("chkRememberDiffAppearancePreference", "Remember the 'Diff appearance' preference", state.RememberAppearance);
        _context = Choice("chkRememberNumberOfContextLines", "Remember the 'Number of context lines' preference", state.RememberContextLines);
        _syntax = Choice("chkRememberShowSyntaxHighlightingInDiff", "Remember the 'Show syntax highlighting' preference", state.RememberSyntaxHighlightingInDiff);
        _gitColoring = Choice("chkUseGitColoring", "Git coloring", state.UseGitColoring);
        _reverse = Choice("chkUseGEThemeGitColoring", "Reverse background color", state.ReverseGitColoring);
        _reverse.IsEnabled = state.UseGitColoring;
        _gitColoring.IsCheckedChanged += (_, _) => _reverse.IsEnabled = _gitColoring.IsChecked == true;
        Tip(_appearance, "chkRememberDiffAppearancePreference", "Diff appearance: patch (default), Git word-diff or Difftastic.");
        Tip(_gitColoring, "chkUseGitColoring", "Use Git coloring engine to show moved code etc.\n");
        Tip(_reverse, "chkUseGEThemeGitColoring", "Color the background at changes (invert colors).");
        Button saveDefault = new()
        {
            Name = "btnSaveCurrentViewSettingsAsDefault", Content = new AccessText { Text = A("btnSaveCurrentViewSettingsAsDefault", "Save current view settings as default") },
            Height = 24, MinHeight = 24, MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(6, 0)
        };
        const string defaultTip = "Saves all current view settings as the default for future sessions.\nNote: The checkboxes 'Remember the \"xyz\" preference' only affect the running instance\nas long as the default has not been saved. These preference values are held in memory\nand must be explicitly saved to become persistent defaults.";
        ToolTip.SetTip(saveDefault, _translations.Translate(Category, "btnSaveCurrentViewSettingsAsDefault", "toolTip", defaultTip));
        saveDefault.Click += (_, _) => ReportSave(_store.SaveCurrentViewSettingsAsDefault());
        StackPanel general = new() { Spacing = 2, Children = { saveDefault, _whitespace, _nonPrinting, _wholeFile, _appearance, _context, _syntax } };
        StackPanel colors = new() { Spacing = 2, Children = { _gitColoring, _reverse } };
        Button ok = Footer("DiffViewerSettingsAccept", "OK"), cancel = Footer("DiffViewerSettingsCancel", "Cancel");
        ok.IsDefault = true; cancel.IsCancel = true;
        ok.Click += (_, _) => { if (ApplySettings()) { Close(true); } };
        cancel.Click += (_, _) => Close(false);
        Content = new StackPanel
        {
            Margin = new Thickness(8), Spacing = 12,
            Children =
            {
                Group("gbGeneral", "General", general), Group("gbDiffColoring", "Diff coloring", colors), _error,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6, Children = { ok, cancel } }
            }
        };
        Opened += (_, _) => { if (Owner is Window owner) { RequestedThemeVariant = owner.ActualThemeVariant; } };
        KeyDown += (_, args) => { if (args.Key == Key.Escape) { args.Handled = true; Close(false); } };
    }

    public static Task<bool> ShowAsync(Window owner, DiffViewerPreferencesStore? store = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        NativeDiffViewerSettingsDialog dialog = new(store) { RequestedThemeVariant = owner.ActualThemeVariant };
        return dialog.ShowDialog<bool>(owner);
    }

    public bool ApplySettings() => ReportSave(_store.SaveSettings(_store.Load() with
    {
        RememberWhitespace = _whitespace.IsChecked == true, RememberWholeFile = _wholeFile.IsChecked == true,
        RememberAppearance = _appearance.IsChecked == true, RememberContextLines = _context.IsChecked == true,
        RememberNonPrintingChars = _nonPrinting.IsChecked == true, RememberSyntaxHighlightingInDiff = _syntax.IsChecked == true,
        UseGitColoring = _gitColoring.IsChecked == true, ReverseGitColoring = _reverse.IsChecked == true
    }));

    private bool ReportSave(bool success)
    {
        _error.IsVisible = !success;
        _error.Text = success ? null : _translations.Translate("Unable to save settings.");
        return success;
    }
    private CheckBox Choice(string name, string source, bool value)
        => new()
        {
            Name = name, Content = new AccessText { Text = A(name, source) }, IsChecked = value,
            Height = 24, MinHeight = 24, Padding = new Thickness(4, 0), VerticalContentAlignment = VerticalAlignment.Center
        };
    private Control Group(string name, string source, Control body)
    {
        Border border = ClassicChrome.Border(body, new Thickness(1), new Thickness(8, 14, 8, 8));
        border.Name = name; border.Margin = new Thickness(0, 7, 0, 0);
        TextBlock caption = new() { Name = name + "Label", Text = T(name, source), Padding = new Thickness(3, 0), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(8, 0, 0, 0) };
        caption.Bind(TextBlock.BackgroundProperty, new DynamicResourceExtension("ClassicWindowBrush"));
        return new Grid { Children = { border, caption } };
    }
    private Button Footer(string name, string source)
        => new() { Name = name, Content = _translations.Translate(source), Width = 75, Height = 24, MinHeight = 24, Padding = new Thickness(4, 0), VerticalContentAlignment = VerticalAlignment.Center };
    private void Tip(Control control, string name, string source) => ToolTip.SetTip(control, _translations.Translate(Category, name, "ToolTipText", source));
    private string T(string name, string source) => _translations.Translate(Category, name, "Text", source);
    private string A(string name, string source) => _translations.TranslateAccessKeys(Category, name, "Text", source);
}
