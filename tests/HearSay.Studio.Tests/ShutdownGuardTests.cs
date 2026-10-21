namespace HearSay.Studio.Tests;

/// <summary>
/// Which exceptions the Studio lets go while it closes: only the one the D-Bus connection of Avalonia throws on Linux
/// when it reports its own disconnection to a dispatcher that has already stopped.
/// </summary>
public sealed class ShutdownGuardTests
{
    /// <summary>A cancellation thrown from a frame of the given assembly, so that its stack trace names it.</summary>
    private static Exception ThrownIn(Action<Action> frame, Exception exception)
    {
        try
        {
            frame(() => throw exception);
        }
        catch (Exception e)
        {
            return e;
        }

        throw new InvalidOperationException("nothing was thrown");
    }

    [Fact]
    public void OnlyTheDBusCancellation_WhileClosing_IsLetGo()
    {
        // The stack of the real one runs through Tmds.DBus; a frame in a lambda of this test stands in for it here.
        Exception dbus = ThrownIn(static throwIt => throwIt(), new TaskCanceledException("A task was canceled."));
        string dbusStack = dbus.StackTrace + Environment.NewLine + "   at Tmds.DBus.Protocol.DBusConnection.Observer.Emit(Exception exception)";

        Assert.True(ShutdownGuard.IsShutdownNoise(dbus, dbusStack, closing: true));

        // Not while the Studio runs, not another exception, not a cancellation from elsewhere.
        Assert.False(ShutdownGuard.IsShutdownNoise(dbus, dbusStack, closing: false));
        Assert.False(ShutdownGuard.IsShutdownNoise(new InvalidOperationException("boom"), dbusStack, closing: true));
        Assert.False(ShutdownGuard.IsShutdownNoise(dbus, dbus.StackTrace, closing: true));
        Assert.False(ShutdownGuard.IsShutdownNoise(null, dbusStack, closing: true));
    }
}
