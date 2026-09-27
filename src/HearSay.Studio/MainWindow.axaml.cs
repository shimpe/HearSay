using Avalonia;
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
        Icon = new WindowIcon(new MemoryStream(HearSayLogo.RenderPng(256)));
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext!;

    private bool _keyboardCentred;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainViewModel viewModel)
        {
            // A new result puts the keyboard back on its target notes.
            viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainViewModel.HasResult))
                {
                    _keyboardCentred = false;
                    Avalonia.Threading.Dispatcher.UIThread.Post(CentreKeyboard, Avalonia.Threading.DispatcherPriority.Loaded);
                }
            };
        }
    }

    private void OnKeyboardLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_keyboardCentred)
        {
            CentreKeyboard();
        }
    }

    /// <summary>
    /// Scrolls the keyboard so that the target notes sit in the middle of the view: halfway between the lowest and
    /// the highest of them, or C4 when there are none.
    /// </summary>
    private void CentreKeyboard()
    {
        double viewport = KeyboardScroll.Viewport.Width;
        if (viewport <= 0 || KeyboardScroll.Extent.Width <= viewport)
        {
            return;
        }

        double centre = DataContext is MainViewModel { TargetNotes.Count: > 0 } viewModel
            ? (Keyboard.CentreOf(viewModel.TargetNotes.Min()) + Keyboard.CentreOf(viewModel.TargetNotes.Max())) / 2
            : Keyboard.CentreOf(60);
        KeyboardScroll.Offset = new Vector(Math.Max(0, centre - (viewport / 2)), 0);
        _keyboardCentred = true;
    }

    private async void OnAdd(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add target notes",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("WAV audio") { Patterns = ["*.wav"] }, FilePickerFileTypes.All],
        });
        foreach (IStorageFile file in files)
        {
            if (file.TryGetLocalPath() is string path)
            {
                await ViewModel.AddAsync(path);
            }
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

    private async void OnPitchChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: TargetItem item })
        {
            await ViewModel.RefreshAsync(item);
        }
    }

    private void OnNotePressed(object? sender, KeyPress e) => ViewModel.PlayNote(e.MidiNote, e.Velocity);

    private void OnPlayTarget(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: TargetItem item })
        {
            ViewModel.PlayTarget(item);
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
