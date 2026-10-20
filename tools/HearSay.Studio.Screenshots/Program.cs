using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HearSay.Editing;
using HearSay.Playing;
using HearSay.Studio;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.Studio.Screenshots;

/// <summary>
/// Renders the screenshots of the HearSay Studio manual: the Studio in a headless window, driven through its view models
/// and controls the way a user would, captured at twice the screen resolution.
///
/// <code>dotnet run -c Release --project tools/HearSay.Studio.Screenshots [-- output-folder]</code>
///
/// It writes to docs/images/studio by default. It runs a real search (FM7, the sample electric piano, a few minutes) and
/// opens the FMX6 structure library of this machine, building it first when it is missing. Favourites and settings go to
/// a temporary folder, so the user's own never show.
/// </summary>
internal static class Program
{
    private const double Scale = 2.0;

    private static string _output = string.Empty;

    [STAThread]
    public static int Main(string[] args)
    {
        string repository = FindRepository();
        _output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(repository, "docs", "images", "studio");
        Directory.CreateDirectory(_output);

        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        string scratch = Path.Combine(Path.GetTempPath(), "hearsay-screenshots-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            Run(repository, scratch);
            Console.WriteLine("Screenshots written to " + _output);
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            return 1;
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static string FindRepository()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, "HearSay.sln")))
            {
                return dir;
            }
        }

        throw new InvalidOperationException("HearSay.sln not found above " + AppContext.BaseDirectory);
    }

    private static void Run(string repository, string scratch)
    {
        using var main = new MainViewModel(new FavouriteSounds(Path.Combine(scratch, "favourites")), Path.Combine(scratch, "studio-settings.json"));
        var window = new MainWindow { DataContext = main, Width = 1600, Height = 1040 };
        window.Show();
        Pump();

        SearchTab(repository, main, window);
        Editing(main, window);
        Formants(main, window);
        LibraryTab(main, window);
        Dialogs(window);

        window.Close();
        Pump();
    }

    /// <summary>The sample electric piano, three notes, searched under FM7.</summary>
    private static void SearchTab(string repository, MainViewModel main, MainWindow window)
    {
        main.SelectedSynthesisTarget = main.SynthesisTargets.Single(t => t.Label == "FM7");
        main.Budget = 4000;
        string folder = Path.Combine(repository, "samples", "targets", "electric-piano-keyed");
        foreach (int note in new[] { 48, 60, 72 })
        {
            Wait(main.AddAsync(Path.Combine(folder, $"electric-piano-keyed-{note}.wav")), TimeSpan.FromMinutes(1));
        }

        Pump();
        TopOfLists(window);
        Capture(window, "search-targets.png");

        main.RunCommand.Execute(null);
        WaitUntil(() => !main.IsRunning && main.HasPatch, TimeSpan.FromMinutes(20), "the search");
        Pump();
        TopOfLists(window);
        Capture(window, "search-result.png");
    }

    /// <summary>The block diagram, a selected operator, and an edit with its marks.</summary>
    private static void Editing(MainViewModel main, MainWindow window)
    {
        PatchView view = Visible<PatchView>(window);
        Click(view.GetVisualDescendants().OfType<TabItem>().First(t => (t.Header as string) == "Block diagram"));
        FmDiagram diagram = Visible<FmDiagram>(window);
        int carrier = Enumerable.Range(0, 6).First(i => main.Edits.DisplayedPatch!.Operators[i].Enabled && main.Edits.DisplayedPatch.OutputLevels[i] > 0);
        SelectOperator(diagram, carrier);
        Capture(window, "block-diagram.png");

        // An edit: another ratio and a new modulator, which the Studio marks against the original.
        EditableValue ratio = main.Edits.SelectedValues.First(v => v.Id.EndsWith(".frequency.ratio", StringComparison.Ordinal));
        main.Edits.Apply(ratio.Set(ratio.Get(main.Edits.DisplayedPatch!) * 2));
        main.Edits.Apply(PatchEdits.AddModulator(carrier));
        Pump();
        SelectOperator(diagram, carrier);
        Capture(window, "edit-marks.png");
        CaptureControl(Visible<PatchInspector>(window), "inspector.png");
    }

    /// <summary>A vowel: an FMX6 operator with a formant filter set by a preset.</summary>
    private static void Formants(MainViewModel main, MainWindow window)
    {
        SynthesisProfile fmx6 = SynthesisProfiles.SuperColliderFmx6;
        OperatorPatch voice = OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Adsr(0.05, 0.2, 0.8, 0.3));
        OperatorMatrixPatch patch = OperatorMatrixPatch.Silent(6).WithOperator(0, voice).WithOutputLevel(0, 0.8);
        main.ShowLibraryPatch(new LibraryPatch("vowel", patch, fmx6, []));
        Pump();
        FmDiagram diagram = Visible<FmDiagram>(window);
        SelectOperator(diagram, 0);
        PatchInspector inspector = Visible<PatchInspector>(window);
        Choose(inspector, "FilterType", "formant");
        Choose(inspector, "FormantSet", "English");
        Click(inspector.GetVisualDescendants().OfType<Button>().First(b => b.Name == "Vowel:hod"));

        // The filter section at the top of the inspector, as a user scrolls to it.
        ScrollViewer scroll = inspector.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "Scroll");
        ComboBox type = inspector.GetVisualDescendants().OfType<ComboBox>().First(c => c.Name == "FilterType");
        double y = type.TranslatePoint(default, (Visual)scroll.Content!)!.Value.Y;
        scroll.Offset = new Vector(0, Math.Max(0, y - 30));
        Pump();
        Capture(window, "formants.png");
        CaptureControl(inspector, "formant-inspector.png");
    }

    /// <summary>The FMX6 library as sound families, a sound clicked, and favourites.</summary>
    private static void LibraryTab(MainViewModel main, MainWindow window)
    {
        main.SelectedTab = 1;
        LibraryViewModel library = main.Library;
        library.SelectedProfile = SynthesisProfiles.SuperColliderFmx6;
        Wait(library.EnsureLoadedAsync(), TimeSpan.FromMinutes(60));
        WaitUntil(() => library.Nodes is { Count: > 0 }, TimeSpan.FromMinutes(10), "the library graph");
        Pump();
        Wait(library.SelectAsync(library.Nodes!.Count / 3), TimeSpan.FromMinutes(1));
        Pump();
        Wait(library.SaveFavouriteAsync("bright pluck", library.ShownPatch!, library.SelectedProfile, "library"), TimeSpan.FromMinutes(1));
        Pump();
        Capture(window, "library.png");
    }

    /// <summary>The two questions the Studio asks.</summary>
    private static void Dialogs(MainWindow window)
    {
        var replace = new ReplaceSoundDialog();
        _ = replace.ShowDialog<bool>(window);
        Pump();
        Capture(replace, "replace-dialog.png");
        replace.Close();

        var close = new CloseDialog(2);
        _ = close.ShowDialog<bool>(window);
        Pump();
        Capture(close, "close-dialog.png");
        close.Close();
        Pump();
    }

    /// <summary>Scrolls every list to its first item, as a user would look at it.</summary>
    private static void TopOfLists(Control root)
    {
        foreach (ListBox list in root.GetVisualDescendants().OfType<ListBox>().Where(l => l.IsEffectivelyVisible && l.ItemCount > 0))
        {
            list.ScrollIntoView(0);
        }

        Pump();
    }

    private static T Visible<T>(Control root) where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => c.IsEffectivelyVisible);

    private static void Choose(Control root, string menuName, string text)
    {
        ComboBox menu = root.GetVisualDescendants().OfType<ComboBox>().First(c => c.Name == menuName && c.IsEffectivelyVisible);
        // Set as a click on its item would: the menu applies the edit on the change of selection.
        menu.SelectedItem = menu.Items.OfType<ComboBoxItem>().First(i => (i.Content as string) == text);
        Pump();
    }

    private static void Click(Control control)
    {
        var top = TopLevel.GetTopLevel(control)!;
        Point at = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top)!.Value;
        top.MouseDown(at, MouseButton.Left);
        top.MouseUp(at, MouseButton.Left);
        Pump();
        top.MouseMove(new Point(2, 2));
        Pump();
    }

    /// <summary>Clicks an operator box of the diagram, where its last render drew it.</summary>
    private static void SelectOperator(FmDiagram diagram, int op)
    {
        FieldInfo field = typeof(FmDiagram).GetField("_parts", BindingFlags.NonPublic | BindingFlags.Instance)!;
        foreach (object entry in (System.Collections.IList)field.GetValue(diagram)!)
        {
            var box = (Rect)entry.GetType().GetField("Item1")!.GetValue(entry)!;
            if (entry.GetType().GetField("Item2")!.GetValue(entry) is PatchPart.OperatorPart { Index: var index } && index == op)
            {
                var top = TopLevel.GetTopLevel(diagram)!;
                Point at = diagram.TranslatePoint(box.Center, top)!.Value;
                top.MouseDown(at, MouseButton.Left);
                top.MouseUp(at, MouseButton.Left);
                Pump();
                top.MouseMove(new Point(2, 2));
                Pump();
                return;
            }
        }

        throw new InvalidOperationException($"OP{op + 1} is not in the diagram");
    }

    private static void Pump()
    {
        for (int i = 0; i < 8; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    /// <summary>Runs the dispatcher until a task ends: its continuations run on the interface thread.</summary>
    private static void Wait(Task task, TimeSpan timeout) => WaitUntil(() => task.IsCompleted, timeout, "a task", task);

    private static void WaitUntil(Func<bool> done, TimeSpan timeout, string what, Task? task = null)
    {
        DateTime end = DateTime.UtcNow + timeout;
        while (!done())
        {
            if (DateTime.UtcNow > end)
            {
                throw new TimeoutException($"Waited {timeout} for {what}.");
            }

            Pump();
            Thread.Sleep(20);
        }

        Pump();
        task?.GetAwaiter().GetResult();
    }

    /// <summary>The whole window (or dialog) at twice its size.</summary>
    private static void Capture(TopLevel top, string name) => Save(top, top.ClientSize, name);

    /// <summary>One control at twice its size.</summary>
    private static void CaptureControl(Control control, string name) => Save(control, control.Bounds.Size, name);

    private static void Save(Visual visual, Size size, string name)
    {
        Pump();
        var pixels = new PixelSize((int)Math.Ceiling(size.Width * Scale), (int)Math.Ceiling(size.Height * Scale));
        using var bitmap = new RenderTargetBitmap(pixels, new Vector(96 * Scale, 96 * Scale));
        bitmap.Render(visual);
        string path = Path.Combine(_output, name);
        bitmap.Save(path);
        Console.WriteLine("  " + name);
    }
}
