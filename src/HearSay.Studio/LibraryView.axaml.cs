using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace HearSay.Studio;

/// <summary>The Library tab. Pickers and the clipboard live here; everything else is in <see cref="LibraryViewModel"/>.</summary>
public sealed partial class LibraryView : UserControl
{
    private bool _keyboardCentred;

    public LibraryView()
    {
        InitializeComponent();
    }

    private LibraryViewModel ViewModel => (LibraryViewModel)DataContext!;

    private void OnNodeClicked(object? sender, int index) => ViewModel.Select(index);

    private async void OnNodeOpened(object? sender, int index) => await ViewModel.OpenAsync(index);

    private async void OnCrumb(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: Crumb crumb })
        {
            await ViewModel.OpenCrumbAsync(crumb);
        }
    }

    private void OnNotePressed(object? sender, KeyPress e) => ViewModel.NoteOn(e.MidiNote, e.Velocity);

    private void OnNoteReleased(object? sender, int midiNote) => ViewModel.NoteOff(midiNote);

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard && ViewModel.Scd.Length > 0)
        {
            await clipboard.SetTextAsync(ViewModel.Scd);
        }
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        IReadOnlyList<IStorageFolder> folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Folder for patch.json, patch.scd and preview.wav",
            AllowMultiple = false,
        });
        if (folders.Count == 1 && folders[0].TryGetLocalPath() is string directory)
        {
            await ViewModel.SaveAsync(directory);
        }
    }

    private void OnShowInSearch(object? sender, RoutedEventArgs e) => ViewModel.ShowInSearch();

    private void OnClearFilter(object? sender, RoutedEventArgs e) => ViewModel.ClearFilter();

    private void OnFavouriteOpening(object? sender, EventArgs e)
    {
        ViewModel.PrepareFavouriteName();
        Avalonia.Threading.Dispatcher.UIThread.Post(() => { FavouriteBox.Focus(); FavouriteBox.SelectAll(); });
    }

    private async void OnSaveFavourite(object? sender, RoutedEventArgs e)
    {
        FavouriteButton.Flyout?.Hide();
        await ViewModel.SaveShownFavouriteAsync();
    }

    private async void OnFavouriteKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Enter)
        {
            FavouriteButton.Flyout?.Hide();
            await ViewModel.SaveShownFavouriteAsync();
        }
    }

    private void OnRemoveShownFavourite(object? sender, RoutedEventArgs e)
    {
        FavouriteButton.Flyout?.Hide();
        ViewModel.RemoveShownFavourite();
    }

    private async void OnFavouriteTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (e.Source is Control { DataContext: FavouriteItem item })
        {
            await ViewModel.PlaceFavouriteAsync(item);
        }
    }

    private void OnRenameOpening(object? sender, EventArgs e)
    {
        ViewModel.FavouriteNickname = ViewModel.SelectedFavourite?.Name ?? string.Empty;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => { RenameBox.Focus(); RenameBox.SelectAll(); });
    }

    private void OnRename(object? sender, RoutedEventArgs e)
    {
        RenameButton.Flyout?.Hide();
        if (ViewModel.SelectedFavourite is { } item)
        {
            ViewModel.RenameFavourite(item, ViewModel.FavouriteNickname);
        }
    }

    private void OnRenameKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Enter)
        {
            OnRename(sender, e);
        }
    }

    private void OnRemoveFavourite(object? sender, RoutedEventArgs e)
    {
        RemoveButton.Flyout?.Hide();
        if (ViewModel.SelectedFavourite is { } item)
        {
            ViewModel.RemoveFavourite(item);
        }
    }


    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        IReadOnlyList<IStorageFolder> folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Folder of exported results (patch.json)",
            AllowMultiple = false,
        });
        if (folders.Count == 1 && folders[0].TryGetLocalPath() is string directory)
        {
            await ViewModel.ImportAsync(directory);
        }
    }

    private async void OnImportTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (e.Source is Control { DataContext: ImportedItem item })
        {
            await ViewModel.PlaceAsync(item);
        }
    }

    /// <summary>Puts A3, the library's reference note, in the middle of the keyboard once it has a size.</summary>
    private void OnKeyboardLayoutUpdated(object? sender, EventArgs e)
    {
        double viewport = KeyboardScroll.Viewport.Width;
        if (_keyboardCentred || viewport <= 0 || KeyboardScroll.Extent.Width <= viewport)
        {
            return;
        }

        KeyboardScroll.Offset = new Vector(Math.Max(0, Keyboard.CentreOf(57) - (viewport / 2)), 0);
        _keyboardCentred = true;
    }
}
