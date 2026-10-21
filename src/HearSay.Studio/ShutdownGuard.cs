namespace HearSay.Studio;

/// <summary>
/// Keeps a known Linux race at shutdown from crashing the Studio. When Avalonia closes its D-Bus connection, Tmds.DBus
/// reports the disconnection to the observers through the synchronization context they were made on, the dispatcher's,
/// which has stopped by then: the report throws a <see cref="TaskCanceledException"/> on a thread-pool thread, and an
/// unhandled exception there ends the process with an abort (exit code 134) after the window has already closed. Once
/// the Studio is closing, that one exception ends the process normally instead; every other exception still crashes as
/// before.
/// </summary>
public static class ShutdownGuard
{
    private static volatile bool _closing;

    /// <summary>Watches for unhandled exceptions; called once at start-up.</summary>
    public static void Install() => AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

    /// <summary>Notes that the Studio is closing: from now on the D-Bus cancellation ends the process normally.</summary>
    public static void Closing() => _closing = true;

    /// <summary>True for the cancellation the D-Bus connection throws while the Studio closes.</summary>
    /// <param name="exception">The unhandled exception.</param>
    /// <param name="stackTrace">Its stack trace.</param>
    /// <param name="closing">True once the Studio is closing.</param>
    public static bool IsShutdownNoise(Exception? exception, string? stackTrace, bool closing) =>
        closing && exception is OperationCanceledException && stackTrace?.Contains("Tmds.DBus", StringComparison.Ordinal) == true;

    private static void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception && IsShutdownNoise(exception, exception.ToString(), _closing))
        {
            // The window is closed and everything is disposed: exit as the Studio would have, without the abort.
            Environment.Exit(0);
        }
    }
}
