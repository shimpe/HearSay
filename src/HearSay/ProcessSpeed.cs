using System.Runtime.InteropServices;

namespace HearSay;

/// <summary>
/// Keeps Windows from running the process in efficiency mode. Windows 11 puts a console process that is not in the
/// foreground, or an app whose window is not, on the efficiency cores at low clocks: building the FMX6 structure library
/// took three times as long (36 instead of 12 minutes for 300 000 noise-free sounds on 24 threads), and searches slow
/// down the same way. The apps call <see cref="RunAtFullSpeed"/> once at start.
/// </summary>
public static class ProcessSpeed
{
    private const int ProcessPowerThrottling = 4;
    private const uint ExecutionSpeed = 1;

    /// <summary>Opts the process out of execution-speed throttling; true when done, false where there is none (not Windows).</summary>
    public static bool RunAtFullSpeed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        // Control the execution speed, and leave it unthrottled.
        var state = new PowerThrottlingState { Version = 1, ControlMask = ExecutionSpeed, StateMask = 0 };
        return NativeMethods.SetProcessInformation(NativeMethods.GetCurrentProcess(), ProcessPowerThrottling, ref state, Marshal.SizeOf<PowerThrottlingState>());
    }

    /// <summary>True unless the process has opted out of execution-speed throttling; for tests.</summary>
    internal static bool IsThrottled()
    {
        var state = new PowerThrottlingState { Version = 1 };
        return !OperatingSystem.IsWindows()
            || !NativeMethods.GetProcessInformation(NativeMethods.GetCurrentProcess(), ProcessPowerThrottling, ref state, Marshal.SizeOf<PowerThrottlingState>())
            || (state.ControlMask & ExecutionSpeed) == 0
            || (state.StateMask & ExecutionSpeed) != 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetProcessInformation(IntPtr process, int informationClass, ref PowerThrottlingState information, int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetProcessInformation(IntPtr process, int informationClass, ref PowerThrottlingState information, int size);
    }
}
