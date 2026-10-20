using Avalonia.Controls;
using Avalonia.Interactivity;

namespace HearSay.Studio;

/// <summary>
/// Asks before the Studio closes while edited sounds are not kept: shown over the main window, it closes with true for
/// Close without saving and false for Cancel (also when it is closed otherwise).
/// </summary>
public sealed partial class CloseDialog : Window
{
    public CloseDialog()
        : this(1)
    {
    }

    /// <summary>A question about a number of edited sounds.</summary>
    /// <param name="unsaved">How many edited sounds would be lost.</param>
    public CloseDialog(int unsaved)
    {
        InitializeComponent();
        Message.Text = unsaved == 1
            ? "An edited sound is not kept (no ☆ favourite, not saved with Save…): its edit is lost when the Studio closes. Cancel, then ☆ or Save… to keep it."
            : $"{unsaved} edited sounds are not kept (no ☆ favourite, not saved with Save…): their edits are lost when the Studio closes. Cancel, then ☆ or Save… to keep them.";
        Opened += (_, _) => CancelButton.Focus();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
