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
            // WAV files on the command line are loaded at start-up, one after the other so that the order is kept.
            string[] files = [.. (desktop.Args ?? []).Where(File.Exists)];
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
