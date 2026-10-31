using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(Juggle.Flash.Tests.TestApp))]

namespace Juggle.Flash.Tests;

/// <summary>The app without a screen, for the tests marked [AvaloniaFact].</summary>
public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
