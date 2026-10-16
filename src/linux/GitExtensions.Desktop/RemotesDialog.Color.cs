using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace GitExtensions.Linux.Desktop;

public sealed partial class RemotesDialog
{
    private void SetColor(string? value)
    {
        _color = string.IsNullOrWhiteSpace(value) ? null : value;
        _resetColor.IsVisible = _color is not null;
        _colorButton.Content = _color is null ? Caption("btnRemoteColor", "Set &color") : new Border
        {
            Name = "RemoteSettingsColorSwatch", Width = 54, Height = 15,
            Background = new SolidColorBrush(Color.TryParse(_color, out Color parsed) ? parsed : Color.Parse("#8b0009"))
        };
        ToolTip.SetTip(_colorButton, _color ?? C("btnRemoteColor", "Set color"));
        Avalonia.Automation.AutomationProperties.SetName(_colorButton, C("btnRemoteColor", "Set color") + (_color is null ? "" : ": " + _color));
    }

    private async Task ChooseColorAsync()
    {
        Color initial = Color.TryParse(_color, out Color parsed) ? parsed : Color.Parse("#8b0009");
        Window picker = new()
        {
            Name = "RemoteColorPicker", Title = T("Color"), Width = 490, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, RequestedThemeVariant = ActualThemeVariant
        };
        TextBox hex = new() { Name = "RemoteColorValue", Text = Html(initial), Height = 24 };
        Border preview = ClassicChrome.Border(new Border { Name = "RemoteColorPreview", Background = new SolidColorBrush(initial), Height = 46 }, new Thickness(1));
        Slider red = Channel("RemoteColorRed", initial.R), green = Channel("RemoteColorGreen", initial.G), blue = Channel("RemoteColorBlue", initial.B);
        Button ok = new() { Name = "AcceptRemoteColor", Content = T("OK"), IsDefault = true, MinWidth = 75 };
        Button cancel = new() { Name = "CancelRemoteColor", Content = T("Cancel"), IsCancel = true, MinWidth = 75 };
        bool loading = false;
        Color selected = initial;
        void Set(Color value)
        {
            loading = true;
            try
            {
                selected = Color.FromRgb(value.R, value.G, value.B); hex.Text = Html(selected);
                red.Value = selected.R; green.Value = selected.G; blue.Value = selected.B;
                ((Border)preview.Child!).Background = new SolidColorBrush(selected); ok.IsEnabled = true;
            }
            finally { loading = false; }
        }
        void FromChannels(object? sender, RangeBaseValueChangedEventArgs args)
        {
            if (!loading) { Set(Color.FromRgb((byte)red.Value, (byte)green.Value, (byte)blue.Value)); }
        }
        red.ValueChanged += FromChannels; green.ValueChanged += FromChannels; blue.ValueChanged += FromChannels;
        hex.PropertyChanged += (_, change) =>
        {
            if (loading || change.Property != TextBox.TextProperty) { return; }
            ok.IsEnabled = Color.TryParse(hex.Text, out Color value) && value.A == 255;
            if (!ok.IsEnabled) { return; }
            loading = true;
            try
            {
                selected = value; red.Value = value.R; green.Value = value.G; blue.Value = value.B;
                ((Border)preview.Child!).Background = new SolidColorBrush(value);
            }
            finally { loading = false; }
        };
        ok.Click += (_, _) => { if (ok.IsEnabled) { picker.Close(Html(selected)); } };
        cancel.Click += (_, _) => picker.Close();
        UniformGrid palette = new() { Name = "RemoteColorPalette", Columns = 8, Rows = 6 };
        string[] colors = ["#ff8080", "#ffff80", "#80ff80", "#00ff80", "#80ffff", "#0080ff", "#ff80c0", "#ff80ff",
            "#ff0000", "#ffff00", "#80ff00", "#00ff40", "#00ffff", "#0080c0", "#8080c0", "#ff00ff",
            "#804040", "#ff8040", "#00ff00", "#008080", "#004080", "#8080ff", "#800040", "#ff0080",
            "#800000", "#ff8000", "#008000", "#008040", "#0000ff", "#0000a0", "#800080", "#8000ff",
            "#400000", "#804000", "#004000", "#004040", "#000080", "#000040", "#400040", "#400080",
            "#000000", "#808000", "#808040", "#808080", "#408080", "#c0c0c0", "#400040", "#ffffff"];
        foreach (string value in colors)
        {
            Color color = Color.Parse(value);
            Button swatch = new() { Name = "RemoteColor" + value[1..], Content = new Border { Background = new SolidColorBrush(color), Width = 22, Height = 18 }, Padding = new Thickness(2), Margin = new Thickness(2) };
            ToolTip.SetTip(swatch, value); Avalonia.Automation.AutomationProperties.SetName(swatch, value);
            swatch.Click += (_, _) => Set(color); palette.Children.Add(swatch);
        }
        Grid channels = new() { ColumnDefinitions = new ColumnDefinitions("58,*,48"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto") };
        (string Caption, Slider Slider)[] entries = [(T("Red"), red), (T("Green"), green), (T("Blue"), blue)];
        for (int i = 0; i < entries.Length; i++)
        {
            TextBlock label = new() { Text = entries[i].Caption, VerticalAlignment = VerticalAlignment.Center };
            TextBlock number = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            Slider slider = entries[i].Slider;
            number.Text = ((byte)slider.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
            slider.ValueChanged += (_, _) => number.Text = ((byte)slider.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
            Grid.SetRow(label, i); Grid.SetRow(slider, i); Grid.SetColumn(slider, 1); Grid.SetRow(number, i); Grid.SetColumn(number, 2);
            channels.Children.Add(label); channels.Children.Add(slider); channels.Children.Add(number);
        }
        StackPanel body = new() { Margin = new Thickness(12), Spacing = 8, Children =
        {
            new TextBlock { Text = T("Basic colors") }, palette, preview, channels,
            new TextBlock { Text = T("Color name or HTML value") }, hex, ExportDialogControls.Buttons(ok, cancel)
        } };
        body.Bind(Panel.BackgroundProperty, new DynamicResourceExtension("ClassicWindowBrush")); picker.Content = body;
        string? result = await picker.ShowDialog<string?>(this);
        if (result is not null) { SetColor(result); }
    }

    private static string Html(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    private static Slider Channel(string name, byte value) => new() { Name = name, Minimum = 0, Maximum = 255, Value = value, TickFrequency = 1, IsSnapToTickEnabled = true, Height = 26 };
}
