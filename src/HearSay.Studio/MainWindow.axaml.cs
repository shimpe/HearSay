using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace HearSay.Studio;

/// <summary>Main window. Pickers and the clipboard live here; everything else is in <see cref="MainViewModel"/>.</summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext!;

    private async void OnLoad(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load a target note",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("WAV audio") { Patterns = ["*.wav"] }, FilePickerFileTypes.All],
        });
        if (files.Count == 1 && files[0].TryGetLocalPath() is string path)
        {
            await ViewModel.LoadAsync(path);
        }
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Folder for patch.json, preview.wav, patch.scd and diagnostics",
            AllowMultiple = false,
        });
        if (folders.Count == 1 && folders[0].TryGetLocalPath() is string directory)
        {
            await ViewModel.SaveAsync(directory);
        }
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is not null && ViewModel.Scd.Length > 0)
        {
            await Clipboard.SetTextAsync(ViewModel.Scd);
        }
    }
}
