namespace HearSay.IntegrationTests;

public sealed class ProcessSpeedTests
{
    [Fact]
    public void RunAtFullSpeed_OptsTheProcessOutOfEfficiencyMode()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.False(ProcessSpeed.RunAtFullSpeed());
            return;
        }

        Assert.True(ProcessSpeed.RunAtFullSpeed());
        Assert.False(ProcessSpeed.IsThrottled());
    }
}
