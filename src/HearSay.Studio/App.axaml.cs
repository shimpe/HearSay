using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace HearSay.Studio;

/// <summary>Application shell: one main window.</summary>
public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            string[] args = desktop.Args ?? [];

            // "--logo file.png [size]" writes the logo and exits, for documentation and packaging.
            int logoAt = Array.IndexOf(args, "--logo");
            if (logoAt >= 0 && logoAt + 1 < args.Length)
            {
                int size = logoAt + 2 < args.Length && int.TryParse(args[logoAt + 2], out int given) ? given : 512;
                File.WriteAllBytes(args[logoAt + 1], HearSayLogo.RenderPng(size));
                var logoModel = new MainViewModel();
                desktop.Exit += (_, _) => logoModel.Dispose();
                desktop.MainWindow = new MainWindow { DataContext = logoModel };
                Avalonia.Threading.Dispatcher.UIThread.Post(desktop.MainWindow.Close);
                base.OnFrameworkInitializationCompleted();
                return;
            }

            var viewModel = new MainViewModel();

            // Closing: from here on the D-Bus race at shutdown ends the process normally (see ShutdownGuard).
            desktop.ShutdownRequested += (_, _) => ShutdownGuard.Closing();
            desktop.Exit += (_, _) => ShutdownGuard.Closing();

            // The live output goes before the audio device it plays on, and the MIDI device is closed.
            desktop.Exit += (_, _) => viewModel.Dispose();
            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            // WAV files on the command line are loaded at start-up, one after the other so that the order is kept.
            string[] files = [.. args.Where(File.Exists)];
            if (files.Length > 0)
            {
                _ = LoadAllAsync(viewModel, files);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task LoadAllAsync(MainViewModel viewModel, string[] files)
    {
        foreach (string path in files)
        {
            await viewModel.AddAsync(path);
        }
    }
}
