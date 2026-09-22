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
            var viewModel = new MainViewModel();
            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            if (desktop.Args is [string path, ..] && File.Exists(path))
            {
                // A WAV file on the command line is loaded at start-up.
                _ = viewModel.LoadAsync(path);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
