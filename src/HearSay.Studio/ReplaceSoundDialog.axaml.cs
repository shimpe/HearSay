using Avalonia.Controls;
using Avalonia.Interactivity;

namespace HearSay.Studio;

/// <summary>
/// Asks before a new sound made from scratch, edited and not kept, is replaced: shown over the main window, it closes with
/// true for Replace and false for Cancel (also when it is closed otherwise).
/// </summary>
public sealed partial class ReplaceSoundDialog : Window
{
    public ReplaceSoundDialog()
    {
        InitializeComponent();
        Opened += (_, _) => ReplaceButton.Focus();
    }

    private void OnReplace(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
