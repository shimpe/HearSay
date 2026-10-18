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
            // A two-way binding would not put the selector back on none after the device chosen could not be opened:
            // the view model's value did not change. The selector is set from it whenever it says so.
            viewModel.Live.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(LivePlayer.SelectedMidiDevice))
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => ShowMidiDevice(viewModel.Live));
                }
            };
            Avalonia.Threading.Dispatcher.UIThread.Post(() => ShowMidiDevice(viewModel.Live));

            // A new result puts the keyboard back on its target notes.
            viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainViewModel.HasPatch))
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

    private void OnNotePressed(object? sender, KeyPress e) => ViewModel.NoteOn(e.MidiNote, e.Velocity);

    private void OnNoteReleased(object? sender, int midiNote) => ViewModel.NoteOff(midiNote);

    /// <summary>The device list is read again whenever it opens: plugging a device in is not always reported.</summary>
    private void OnMidiDropDownOpened(object? sender, EventArgs e) => ViewModel.Live.RefreshMidiDevices();

    /// <summary>A device chosen in the selector is opened; a selection lost while the list changes is put back.</summary>
    private void OnMidiSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        if (MidiSelector.SelectedItem is string device)
        {
            viewModel.Live.SelectedMidiDevice = device;
        }
        else
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => ShowMidiDevice(viewModel.Live));
        }
    }

    private void ShowMidiDevice(LivePlayer live)
    {
        if (!Equals(MidiSelector.SelectedItem, live.SelectedMidiDevice))
        {
            MidiSelector.SelectedItem = live.SelectedMidiDevice;
        }
    }

    private void OnPlayTarget(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: TargetItem item })
        {
            ViewModel.PlayTarget(item);
        }
    }

    private async void OnFindInLibrary(object? sender, RoutedEventArgs e) => await ViewModel.FindInLibraryAsync();

    private void OnFavouriteOpening(object? sender, EventArgs e)
    {
        ViewModel.PrepareFavouriteName();
        Avalonia.Threading.Dispatcher.UIThread.Post(() => { FavouriteBox.Focus(); FavouriteBox.SelectAll(); });
    }

    private async void OnSaveFavourite(object? sender, RoutedEventArgs e)
    {
        FavouriteButton.Flyout?.Hide();
        await ViewModel.SaveFavouriteAsync();
    }

    private async void OnFavouriteKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Enter)
        {
            FavouriteButton.Flyout?.Hide();
            await ViewModel.SaveFavouriteAsync();
        }
    }

    private void OnRemoveShownFavourite(object? sender, RoutedEventArgs e)
    {
        FavouriteButton.Flyout?.Hide();
        ViewModel.RemoveShownFavourite();
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is not null && ViewModel.Scd.Length > 0)
        {
            await Clipboard.SetTextAsync(ViewModel.Scd);
        }
    }
}
