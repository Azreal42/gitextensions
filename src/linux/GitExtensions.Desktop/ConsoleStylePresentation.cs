using System.Text.Json;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace GitExtensions.Linux.Desktop;

/// <summary>Saved presentation only; no output/text/caret mutation and no lifetime beyond a real attachment.</summary>
internal static class ConsoleStylePresentation
{
    private static readonly ConditionalWeakTable<TextBox, PaletteBindings> Palettes = new();
    private sealed class PaletteBindings
    {
        internal IDisposable? Background, Foreground;
        internal PaletteBindings(TextBox output)
        {
            // Fluent hover/focus styles set the template Border, independently of TextBox.Background.
            output.TemplateApplied += (_, args) => BindBorder(args.NameScope.Find<Border>("PART_BorderElement"));
            BindBorder(output.GetVisualDescendants().OfType<Border>().FirstOrDefault(control => control.Name == "PART_BorderElement"));
            void BindBorder(Border? border)
            {
                if (border is not null) { border.Bind(Border.BackgroundProperty, new Binding(nameof(TextBox.Background)) { Source = output }); }
            }
        }
    }
    private static event Action<string, ConsoleStylePreferences>? Saved;
    internal static void PublishSaved(string profile, ConsoleStylePreferences value) => Saved?.Invoke(Path.GetFullPath(profile), value);

    internal static void Bind(TextBox output, ConsoleStylePreferencesStore? preferences = null, ResolvedDesktopFont? defaultFont = null)
    {
        ResolvedDesktopFont fallback = defaultFont ?? DesktopFonts.For(DesktopFontRole.Monospace);
        DesktopFontResolver.Apply(output, fallback);
        Bind(output, value => Apply(output, value, fallback), preferences);
    }

    internal static void Bind(Control output, Action<ConsoleStylePreferences> apply, ConsoleStylePreferencesStore? preferences = null)
    {
        ConsoleStylePreferencesStore store = preferences ?? new(); string profile = Path.GetFullPath(store.FilePath);
        bool attached = false; long attachment = 0;
        void Read()
        {
            try { apply(store.Load()); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            { /* Settings reports corrupt/unreadable profiles. A consumer never repairs them. */ }
        }
        void Changed(string path, ConsoleStylePreferences value)
        {
            long version = Interlocked.Read(ref attachment);
            if (!Volatile.Read(ref attached) || path != profile) { return; }
            if (Dispatcher.UIThread.CheckAccess()) { apply(value); }
            else { Dispatcher.UIThread.Post(() => { if (Volatile.Read(ref attached) && Interlocked.Read(ref attachment) == version) { apply(value); } }); }
        }
        Read();
        output.AttachedToVisualTree += (_, _) =>
        { if (!attached) { Interlocked.Increment(ref attachment); attached = true; Saved += Changed; Read(); } };
        output.DetachedFromVisualTree += (_, _) => { Interlocked.Increment(ref attachment); attached = false; Saved -= Changed; };
    }

    internal static void Apply(TextBox output, ConsoleStylePreferences value, ResolvedDesktopFont? fallback = null)
    {
        PaletteBindings bindings = Palettes.GetValue(output, static textBox => new PaletteBindings(textBox));
        // A local SetValue does not retire a DynamicResource binding; it can emit again on reattachment.
        bindings.Background?.Dispose(); bindings.Foreground?.Dispose(); bindings.Background = bindings.Foreground = null;
        if (value.Font is null) { DesktopFontResolver.Apply(output, fallback ?? DesktopFonts.For(DesktopFontRole.Monospace)); }
        else { DesktopFontResolver.Apply(output, DesktopFontResolver.Resolve(value.Font, DesktopFontRole.Monospace)); }
        if (value.Style == "Light") { output.Background = Brushes.White; output.Foreground = Brushes.Black; }
        else if (value.Style == "Dark") { output.Background = new SolidColorBrush(Color.Parse("#0c0c0c")); output.Foreground = new SolidColorBrush(Color.Parse("#cccccc")); }
        else
        {
            bindings.Background = output.Bind(TextBox.BackgroundProperty, new DynamicResourceExtension("ClassicEditorBrush"));
            bindings.Foreground = output.Bind(TextBox.ForegroundProperty, new DynamicResourceExtension("ClassicTextBrush"));
        }
    }
}
