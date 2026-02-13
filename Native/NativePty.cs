using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ClaudeToiletClient.Native;

public static class NativePty
{
    // Our native helper — forkpty+exec happens entirely in C, never in managed code
    [DllImport("libptyhelper.so", SetLastError = true)]
    private static extern int spawn_in_pty(
        [MarshalAs(UnmanagedType.LPStr)] string file,
        IntPtr argv,
        int cols, int rows,
        ref int masterFd);

    [DllImport("libptyhelper.so", SetLastError = true)]
    private static extern int resize_pty(int masterFd, int cols, int rows);

    [DllImport("libc.so.6", SetLastError = true)]
    private static extern int kill(int pid, int signal);

    [DllImport("libc.so.6", SetLastError = true)]
    private static extern int waitpid(int pid, ref int status, int options);

    [DllImport("libc.so.6", SetLastError = true)]
    private static extern int close(int fd);

    private const int WNOHANG = 1;
    private const int SIGTERM = 15;
    private const int SIGKILL = 9;

    public static (int MasterFd, int ChildPid) ForkWithPty(
        string command, string[] args, int cols, int rows)
    {
        // Build null-terminated argv array for execvp: [command, ...args, null]
        var allArgs = new string[args.Length + 2];
        allArgs[0] = command;
        for (int i = 0; i < args.Length; i++)
            allArgs[i + 1] = args[i];
        allArgs[^1] = null!;

        // Marshal to native string array
        var argPtrs = new IntPtr[allArgs.Length];
        for (int i = 0; i < allArgs.Length; i++)
        {
            argPtrs[i] = allArgs[i] != null
                ? Marshal.StringToHGlobalAnsi(allArgs[i])
                : IntPtr.Zero;
        }

        var argvHandle = GCHandle.Alloc(argPtrs, GCHandleType.Pinned);
        int masterFd = -1;

        try
        {
            int pid = spawn_in_pty(command, argvHandle.AddrOfPinnedObject(),
                cols, rows, ref masterFd);

            if (pid < 0)
            {
                int errno = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"spawn_in_pty failed with errno {errno}");
            }

            return (masterFd, pid);
        }
        finally
        {
            argvHandle.Free();
            foreach (var ptr in argPtrs)
            {
                if (ptr != IntPtr.Zero)
                    Marshal.FreeHGlobal(ptr);
            }
        }
    }

    public static void SetWindowSize(int masterFd, int cols, int rows)
    {
        resize_pty(masterFd, cols, rows);
    }

    public static void KillProcess(int pid)
    {
        kill(pid, SIGTERM);
        int status = 0;
        int result = waitpid(pid, ref status, WNOHANG);
        if (result == 0)
        {
            Thread.Sleep(100);
            kill(pid, SIGKILL);
            waitpid(pid, ref status, 0);
        }
    }

    public static bool IsProcessRunning(int pid)
    {
        int status = 0;
        int result = waitpid(pid, ref status, WNOHANG);
        return result == 0;
    }

    public static void CloseFd(int fd)
    {
        close(fd);
    }

    public static FileStream CreateReadStream(int fd)
    {
        var safeHandle = new SafeFileHandle((IntPtr)fd, ownsHandle: false);
        return new FileStream(safeHandle, FileAccess.Read, bufferSize: 4096, isAsync: false);
    }

    public static FileStream CreateWriteStream(int fd)
    {
        var safeHandle = new SafeFileHandle((IntPtr)fd, ownsHandle: false);
        return new FileStream(safeHandle, FileAccess.Write, bufferSize: 0, isAsync: false);
    }
}
