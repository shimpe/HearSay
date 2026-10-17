using Avalonia;

namespace HearSay.Studio;

/// <summary>Entry point of the desktop app.</summary>
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Searches and library builds run on every core at full clock, also while the window is not in front.
        ProcessSpeed.RunAtFullSpeed();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Also used by the Avalonia designer.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
