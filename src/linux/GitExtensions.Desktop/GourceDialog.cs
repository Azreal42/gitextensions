using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using GitExtensions.Linux.Plugins;

namespace GitExtensions.Linux.Desktop;

/// <summary>Native equivalent of GourceStart's three rows, Browse controls, links and Start.</summary>
public sealed class GourceDialog : RepositoryDialog
{
    private readonly TextBox _path = new() { Name = "GourcePath", Height = 24 };
    private readonly TextBox _directory = new() { Name = "WorkingDir", Height = 24 };
    private readonly TextBox _arguments = new() { Name = "Arguments", Height = 24 };
    private readonly GourcePlugin _plugin;
    private readonly Button _start;
    private readonly List<Button> _links = [];
    private bool _validRepository;
    public GourceLaunch? LastLaunch { get; private set; }
    public GourceOptions CurrentOptions => new(_path.Text ?? "", _arguments.Text ?? "", _directory.Text);
    public IReadOnlyDictionary<string, string?> CurrentValues => new Dictionary<string, string?>
    {
        ["executable"] = CurrentOptions.Executable, ["arguments"] = CurrentOptions.Arguments, ["workingDirectory"] = CurrentOptions.WorkingDirectory
    };

    public GourceDialog(GitRepository repository, IReadOnlyDictionary<string, string?>? savedValues = null,
        IGourceAvatarProvider? avatarProvider = null) : base(repository, "Gource")
    {
        _plugin = new(avatarProvider ?? new GourceAvatarProvider());
        _path.Text = savedValues?.GetValueOrDefault("executable") ?? "gource";
        _directory.Text = savedValues?.GetValueOrDefault("workingDirectory") is { Length: > 0 } directory ? directory : repository.Path;
        _arguments.Text = savedValues?.GetValueOrDefault("arguments") ?? new GourceOptions().Arguments;
        Width = 574; Height = 170; MinWidth = 460; MinHeight = 170;
        Grid fields = new() { Name = "GourceFields", ColumnDefinitions = new("100,*,100"), RowDefinitions = new("26,26,26"), Margin = new Thickness(12, 12, 12, 3) };
        AddField(fields, 0, "Path to Gource", _path);
        AddField(fields, 1, "Repository", _directory);
        AddField(fields, 2, "Arguments", _arguments); Grid.SetColumnSpan(_arguments, 2);
        Button executableBrowse = Action("GourceBrowse", "Browse", BrowseExecutableAsync);
        Grid.SetRow(executableBrowse, 0); Grid.SetColumn(executableBrowse, 2); executableBrowse.Margin = new Thickness(3, 0); executableBrowse.Height = 24; fields.Children.Add(executableBrowse);
        Button directoryBrowse = Action("WorkingDirBrowse", "Browse", BrowseDirectoryAsync);
        Grid.SetRow(directoryBrowse, 1); Grid.SetColumn(directoryBrowse, 2); directoryBrowse.Margin = new Thickness(3, 0); directoryBrowse.Height = 24; fields.Children.Add(directoryBrowse);
        DockPanel.SetDock(fields, Dock.Top); Root.Children.Add(fields);
        Grid bottom = new() { ColumnDefinitions = new("*,75"), Margin = new Thickness(12, 3, 12, 8) };
        StackPanel links = new() { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        links.Children.Add(Link("GourceProject", "Gource project", "https://github.com/acaudwell/Gource/"));
        links.Children.Add(Link("GourceCommandLine", "Gource command line", "https://github.com/acaudwell/Gource#readme"));
        bottom.Children.Add(links);
        _start = Action("StartGource", "Start", StartAsync); _start.IsDefault = true; _start.Height = 24;
        Grid.SetColumn(_start, 1); bottom.Children.Add(_start); Root.Children.Add(bottom);
        Track(_path, _directory, _arguments);
        Opened += (_, _) => UpdateLinkColors();
        PropertyChanged += (_, change) => { if (change.Property == ActualThemeVariantProperty) UpdateLinkColors(); };
        UpdateLinkColors();
        UpdateActions();
    }

    public override Task InitializeAsync() => RunAsync("Gource", async token =>
    {
        _validRepository = await Repository.IsRepositoryAsync(token);
        Status.Text = _validRepository ? "" : T("Gource can only be started from a valid Git repository.");
    });

    private async Task StartAsync()
    {
        GourceOptions options = CurrentOptions;
        string directory = string.IsNullOrWhiteSpace(options.WorkingDirectory) ? Repository.Path : Path.GetFullPath(options.WorkingDirectory);
        string? executable = GourceExecutable.Resolve(options.Executable, directory);
        if (executable is null) { Status.Text = T("Cannot find Gource.") + "\n" + T("Please install Gource and set the correct path."); _path.Focus(); return; }
        await RunAsync("Start Gource", async token =>
        {
            LastLaunch = await _plugin.LaunchAsync(Repository, options with { Executable = executable }, token);
            Status.Text = T("Gource started");
        });
        if (LastLaunch is not null && LastError is null) Close();
    }

    private async Task BrowseExecutableAsync()
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = T("Path to Gource"), AllowMultiple = false });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) _path.Text = path;
    }

    private async Task BrowseDirectoryAsync()
    {
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = T("Repository"), AllowMultiple = false });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } directory) _directory.Text = directory;
    }

    private Button Link(string name, string label, string url)
    {
        Button button = Action(name, label, () =>
        {
            Process.Start(new ProcessStartInfo("xdg-open") { UseShellExecute = false, ArgumentList = { url } });
            return Task.CompletedTask;
        });
        button.Content = new TextBlock { Text = T(label), TextDecorations = TextDecorations.Underline };
        button.Background = Brushes.Transparent; button.BorderThickness = new Thickness(0); button.Padding = new Thickness(3, 0); button.MinHeight = 0;
        _links.Add(button);
        return button;
    }

    private void UpdateLinkColors()
    {
        Color background = this.TryFindResource("ClassicWindowBrush", ActualThemeVariant, out object? value) && value is ISolidColorBrush brush
            ? brush.Color : Colors.White;
        // ThemeFix.SetupLinkLabel calls ColorHelper.AdaptForeColor on the default
        // blue LinkLabel color. Port its exact 10% RGB steps and 4.5 contrast target.
        Color original = Colors.Blue;
        Color adapted = original;
        if (Contrast(background, original) < 4.5)
        {
            bool brighter = Luminance(original) >= Luminance(background);
            Color first = Adjust(original, brighter);
            Color second = Contrast(background, first) >= 4.5 ? first : Adjust(original, !brighter);
            adapted = Contrast(background, first) >= Contrast(background, second) ? first : second;
        }
        foreach (Button link in _links) link.Foreground = new SolidColorBrush(adapted);

        Color Adjust(Color color, bool increase)
        {
            int r = color.R, g = color.G, b = color.B;
            while (Contrast(background, color) < 4.5 && (increase ? r < 255 || g < 255 || b < 255 : r > 0 || g > 0 || b > 0))
            {
                int Channel(int channel) => increase ? Math.Min(255, channel + (int)Math.Ceiling((255 - channel) * .1)) : channel - (int)Math.Ceiling(channel * .1);
                r = Channel(r); g = Channel(g); b = Channel(b);
                color = Color.FromArgb(color.A, (byte)r, (byte)g, (byte)b);
            }
            return color;
        }
        static double Luminance(Color color)
        {
            static double Linear(byte value) { double c = value / 255d; return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4); }
            return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        }
        static double Contrast(Color first, Color second)
        { double a = Luminance(first), b = Luminance(second); return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05); }
    }

    private void AddField(Grid grid, int row, string label, Control input)
    {
        TextBlock text = new() { Text = T(label), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0) };
        Grid.SetRow(text, row); grid.Children.Add(text);
        input.Margin = new Thickness(3, 0); Grid.SetRow(input, row); Grid.SetColumn(input, 1); grid.Children.Add(input);
    }

    protected override void UpdateActions() => _start.IsEnabled = _validRepository;
}
