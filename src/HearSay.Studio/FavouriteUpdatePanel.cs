using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace HearSay.Studio;

/// <summary>
/// The part of the favourite box that offers to put the shown edit in the place of the favourite it was made from, with a
/// second click to confirm, since the favourite's old version is lost. Hidden when no edit of a favourite is shown.
/// </summary>
public sealed class FavouriteUpdatePanel : StackPanel
{
    private readonly TextBlock _text = new() { Classes = { "muted" }, FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly Button _update = new() { Name = "UpdateFavourite", HorizontalAlignment = HorizontalAlignment.Right };
    private readonly StackPanel _confirm = new() { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
    private string _name = string.Empty;

    public FavouriteUpdatePanel()
    {
        Spacing = 4;
        Margin = new Thickness(0, 6, 0, 0);
        IsVisible = false;
        var overwrite = new Button { Name = "OverwriteFavourite", Content = "Overwrite" };
        if (Application.Current?.FindResource("AmberBrush") is IBrush amber)
        {
            overwrite.Foreground = amber;
        }

        var cancel = new Button { Content = "Cancel" };
        _confirm.Children.Add(cancel);
        _confirm.Children.Add(overwrite);
        Children.Add(_text);
        Children.Add(_update);
        Children.Add(_confirm);
        _update.Click += (_, _) => Ask(true);
        cancel.Click += (_, _) => Ask(false);
        overwrite.Click += (_, _) =>
        {
            Ask(false);
            Update?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>Raised when the update is confirmed.</summary>
    public event EventHandler? Update;

    /// <summary>Shows the offer for the favourite the shown edit was made from, or hides it.</summary>
    /// <param name="favourite">Its nickname; null when no edit of a favourite is shown.</param>
    public void ShowFor(string? favourite)
    {
        _name = favourite ?? string.Empty;
        IsVisible = favourite is not null;
        _update.Content = $"Update “{_name}”";
        ToolTip.SetTip(_update, $"Put this edit in the place of the favourite {_name}: same nickname, the edited sound");
        Ask(false);
    }

    private void Ask(bool asking)
    {
        _text.Text = asking
            ? $"Overwrite “{_name}” with this edit? Its old version is lost."
            : $"This is an edit of the favourite “{_name}”.";
        _update.IsVisible = !asking;
        _confirm.IsVisible = asking;
    }
}
