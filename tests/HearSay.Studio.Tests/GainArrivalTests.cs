using HearSay.Playing;

namespace HearSay.Studio.Tests;

/// <summary>
/// The code view of either tab gets its "// Level:" line when the gain of the sound shown arrives, not only when the
/// sound is shown. The gain comes from a fake the test completes; the code is refreshed by a job posted to the interface
/// thread's dispatcher, which the test runs.
/// </summary>
[Collection("InterfaceThread")]
public sealed class GainArrivalTests : IDisposable
{
    private const float Gain = 3.25f;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hearsay-studio-gain-" + Guid.NewGuid().ToString("N"));

    private readonly TaskCompletionSource<float> _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public GainArrivalTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private MainViewModel Main() =>
        new(new FavouriteSounds(Path.Combine(_directory, "favourites")), Path.Combine(_directory, "studio-settings.json"), _ => _pending.Task);

    private void CompleteAndPump()
    {
        _pending.SetResult(Gain);
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }
    }

    [Fact]
    public void CodeView_GetsTheLevelLine_WhenTheGainArrives()
    {
        using MainViewModel main = Main();
        main.StartFromScratch();
        Assert.Null(main.Edits.DisplayedGain);
        Assert.DoesNotContain("// Level:", main.Scd, StringComparison.Ordinal);

        CompleteAndPump();

        Assert.Equal(Gain, main.Edits.DisplayedGain);
        Assert.Contains("// Level: x 3.25,", main.Scd,StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryCodeView_GetsTheLevelLine_WhenTheGainArrives()
    {
        using MainViewModel main = Main();
        LibraryViewModel library = main.Library;
        library.StartFromScratch();
        Assert.Null(library.Edits.DisplayedGain);
        Assert.DoesNotContain("// Level:", library.Scd, StringComparison.Ordinal);

        CompleteAndPump();

        Assert.Equal(Gain, library.Edits.DisplayedGain);
        Assert.Contains("// Level: x 3.25,", library.Scd, StringComparison.Ordinal);
    }
}
