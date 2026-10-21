using Avalonia.Controls;
using Avalonia.Interactivity;

namespace HearSay.Studio;

/// <summary>
/// Asks before a structure library is built for the first time, saying what it costs: shown over the main window, it
/// closes with Build, or (before a search) Search without it, or Cancel (also when it is closed otherwise). Before the
/// Library tab builds one, Cancel reads Not now and there is no Search without it.
/// </summary>
public sealed partial class LibraryBuildDialog : Window
{
    public LibraryBuildDialog()
    {
        InitializeComponent();
        Opened += (_, _) => BuildButton.Focus();
    }

    /// <summary>A question about building libraries.</summary>
    /// <param name="question">What would be built, and what it costs.</param>
    /// <param name="beforeSearch">True before a search, which can go on without the library.</param>
    public LibraryBuildDialog(LibraryBuildQuestion question, bool beforeSearch)
        : this()
    {
        ArgumentNullException.ThrowIfNull(question);
        Heading.Text = $"Build the {question.Names} {(question.Profiles.Count == 1 ? "library" : "libraries")} first?";
        Message.Text = question.Text;
        WithoutButton.IsVisible = beforeSearch;
        CancelButton.Content = beforeSearch ? "Cancel" : "Not now";
        BuildButton.Content = beforeSearch ? "Build and search" : "Build now";
    }

    private void OnBuild(object? sender, RoutedEventArgs e) => Close(LibraryBuildAnswer.Build);

    private void OnWithout(object? sender, RoutedEventArgs e) => Close(LibraryBuildAnswer.WithoutLibrary);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(LibraryBuildAnswer.Cancel);
}
