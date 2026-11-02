using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ChasePie.Lookout.Runner.Services;

public static partial class ProcessSignalExtensions
{
    private const int ESRCH = 3; // No such process

    /// <summary>
    /// Sends a POSIX signal to the process so it can shut down gracefully (unlike <see cref="Process.Kill()"/>, which sends SIGKILL).
    /// Does nothing if the process has already exited.
    /// </summary>
    /// <exception cref="NotImplementedException">On Windows, which has no POSIX signals.</exception>
    public static void SendSignal(this Process process, PosixSignal signal)
    {
        ArgumentNullException.ThrowIfNull(process);

        if (OperatingSystem.IsWindows())
        {
            // TODO: Windows has no signals; graceful shutdown needs a console Ctrl+C/Ctrl+Break approach.
            throw new NotImplementedException("Sending signals to processes is not implemented on Windows yet.");
        }

        if (process.HasExited)
        {
            return;
        }

        if (Kill(process.Id, ToSignalNumber(signal)) == 0)
        {
            return;
        }

        var errno = Marshal.GetLastPInvokeError();
        if (errno == ESRCH)
        {
            return; // Exited between the HasExited check and kill()
        }

        throw new Win32Exception(errno, $"Failed to send {signal} to process {process.Id}.");
    }

    // PosixSignal values are abstract (negative); these four have the same number on Linux and macOS.
    private static int ToSignalNumber(PosixSignal signal) => signal switch
    {
        PosixSignal.SIGHUP => 1,
        PosixSignal.SIGINT => 2,
        PosixSignal.SIGQUIT => 3,
        PosixSignal.SIGTERM => 15,
        _ => throw new ArgumentOutOfRangeException(nameof(signal), signal, "Only SIGHUP, SIGINT, SIGQUIT and SIGTERM are supported."),
    };

    // Verified to resolve on macOS, glibc (Ubuntu) and musl (Alpine)
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Kill(int pid, int signal);
}
