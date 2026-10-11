using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;
using GitExtensions.Linux.Desktop;
using NUnit.Framework;
using System.Globalization;

namespace GitExtensions.Desktop.Tests;

[NonParallelizable]
public class AskpassTests
{
    [AvaloniaTest]
    public void HelperRestoresUiCultureAndInvalidCultureKeepsTheCurrentLocale()
    {
        CultureInfo previous = CultureInfo.CurrentUICulture;
        string? previousEnvironment = Environment.GetEnvironmentVariable("GITEXTENSIONS_UI_CULTURE");
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            Environment.SetEnvironmentVariable("GITEXTENSIONS_UI_CULTURE", "fr-FR");
            AskpassApplication.ApplyUiCulture();
            Assert.That(CultureInfo.CurrentUICulture.Name, Is.EqualTo("fr-FR"));
            AskpassWindow window = new("Fake authentication request");
            window.Show();
            try { Assert.That(Field<Button>(window, "CancelAskpass").Content, Is.EqualTo("Annuler")); }
            finally { window.Close(); }
            Environment.SetEnvironmentVariable("GITEXTENSIONS_UI_CULTURE", "invalid/culture/value");
            AskpassApplication.ApplyUiCulture();
            Assert.That(CultureInfo.CurrentUICulture.Name, Is.EqualTo("fr-FR"));
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
            Environment.SetEnvironmentVariable("GITEXTENSIONS_UI_CULTURE", previousEnvironment);
        }
    }

    [TestCase("Light")]
    [TestCase("Dark")]
    [TestCase("System")]
    [TestCase("broken")]
    public void AskpassUsesTheSavedDesktopThemeAndHandlesUnreadablePreferences(string theme)
    {
        string directory = Path.Combine(Path.GetTempPath(), "gitextensions-askpass-theme-" + Guid.NewGuid().ToString("N"));
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", directory);
            PreferencesStore store = new();
            store.Save(new DesktopPreferences { Theme = theme });
            if (theme == "broken") { File.WriteAllText(store.FilePath, "unreadable json"); }
            ThemeVariant expected = theme switch { "Dark" => ThemeVariant.Dark, "System" => ThemeVariant.Default, _ => ThemeVariant.Light };
            Assert.That(AskpassApplication.ReadPreferredTheme(), Is.EqualTo(expected));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaTest]
    public void UsernameIsVisibleAndSecretsAreMaskedByDefault()
    {
        foreach ((string prompt, bool masked) in new[]
        {
            ("Username for 'https://example.invalid': ", false),
            ("Password for 'https://example.invalid': ", true),
            ("Enter passphrase for key '/fake/key': ", true),
            ("Unknown authentication request", true)
        })
        {
            AskpassWindow window = new(prompt);
            window.Show();
            try
            {
                TextBox input = Field<TextBox>(window, "AskpassInput");
                Assert.That(input.PasswordChar != '\0', Is.EqualTo(masked), prompt);
                Assert.That(input.RevealPassword, Is.False);
                Assert.That(input.AcceptsReturn || input.AcceptsTab, Is.False);
                Assert.That(Field<TextBlock>(window, "AskpassPrompt").Text, Is.EqualTo(prompt));
                Assert.That(window.Width, Is.EqualTo(450));
                Assert.That(input.Bounds.Height, Is.InRange(24, 32));
            }
            finally { window.Close(); }
        }
    }

    [AvaloniaTest]
    public void AcceptedFakeResponseIsWrittenExactlyOnceAndInputIsCleared()
    {
        AskpassWindow window = new("Password for fake service: ");
        window.Show();
        const string fake = "fictitious password λ '$() & ";
        TextBox input = Field<TextBox>(window, "AskpassInput");
        input.Text = fake;
        Field<Button>(window, "AcceptAskpass").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        using StringWriter output = new();
        Assert.Multiple(() =>
        {
            Assert.That(window.Response, Is.EqualTo(fake));
            Assert.That(input.Text, Is.Empty);
            Assert.That(AskpassApplication.WriteResponse(window.Response, output), Is.Zero);
            Assert.That(output.ToString(), Is.EqualTo(fake + "\n"));
        });
    }

    [AvaloniaTest]
    public void CancelAndWindowCloseReturnFailureWithoutResponse()
    {
        foreach (bool cancelButton in new[] { true, false })
        {
            AskpassWindow window = new("Enter fake passphrase: ");
            window.Show();
            TextBox input = Field<TextBox>(window, "AskpassInput");
            input.Text = "fictitious cancelled response";
            if (cancelButton) { Field<Button>(window, "CancelAskpass").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
            else { window.Close(); }
            using StringWriter output = new();
            Assert.Multiple(() =>
            {
                Assert.That(window.Response, Is.Null);
                Assert.That(input.Text, Is.Empty);
                Assert.That(AskpassApplication.WriteResponse(window.Response, output), Is.EqualTo(1));
                Assert.That(output.ToString(), Is.Empty);
            });
        }
    }

    [TestCase("line\nbreak")]
    [TestCase("carriage\rreturn")]
    [TestCase("nul\0byte")]
    public void InvalidProtocolResponsesAreRejectedWithoutWriting(string value)
    {
        using StringWriter output = new();
        Assert.That(AskpassApplication.WriteResponse(value, output), Is.EqualTo(1));
        Assert.That(output.ToString(), Is.Empty);
    }

    private static T Field<T>(Window window, string name) where T : Control
        => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
}
