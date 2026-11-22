using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

internal static class UuMonitor
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    private static volatile bool armed;
    private static volatile bool disconnected;

    private static void ReadCommands()
    {
        string command;
        while ((command = Console.ReadLine()) != null)
        {
            if (command == "arm")
            {
                armed = true;
                Console.WriteLine("armed");
            }
            else if (command.StartsWith("bottom ", StringComparison.Ordinal))
            {
                long handle;
                if (long.TryParse(command.Substring(7), out handle) && handle != 0)
                {
                    SetWindowPos(new IntPtr(handle), new IntPtr(1), 0, 0, 0, 0, 0x0013);
                }
            }
        }
        disconnected = true;
    }

    private static bool IsRunning(string processName)
    {
        Process[] processes = Process.GetProcessesByName(processName);
        foreach (Process process in processes) process.Dispose();
        return processes.Length > 0;
    }

    private static int Main(string[] args)
    {
        if (args.Length != 4) return 2;
        int parentId;
        if (!int.TryParse(args[2], out parentId)) return 2;
        new Thread(ReadCommands) { IsBackground = true }.Start();
        bool? previous = null;
        for (;;)
        {
            if (disconnected && !armed) return 0;
            bool active = IsRunning(args[3]);
            if (!disconnected && previous != active) Console.WriteLine(active ? "uu on" : "uu off");
            if (disconnected && !active)
            {
                try
                {
                    using (Process parent = Process.GetProcessById(parentId))
                    {
                        if (!parent.WaitForExit(3000)) continue;
                    }
                }
                catch (ArgumentException) {}
                // Project path is a trusted app-owned directory and cannot contain quotes.
                Process.Start(new ProcessStartInfo
                {
                    FileName = args[0],
                    Arguments = "\"" + args[1] + "\" --resume-desktop",
                    WorkingDirectory = args[1],
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                return 0;
            }
            previous = active;
            Thread.Sleep(disconnected ? 2200 : 1500);
        }
    }
}
