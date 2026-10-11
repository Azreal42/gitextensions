using Avalonia;
using Avalonia.Headless;
using GitExtensions.Linux.Desktop;

[assembly: AvaloniaTestApplication(typeof(GitExtensions.Desktop.Tests.TestAppBuilder))]

namespace GitExtensions.Desktop.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .WithInterFont()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
