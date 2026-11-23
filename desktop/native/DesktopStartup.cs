using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32;

namespace Xdc.NativeDesktop;

internal static class DesktopStartup
{
    public const string AppId = "Alfred.xdc.Desktop";
    private const string LinkName = "学习桌面.lnk";
    private const string ApprovalKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
    private static readonly Guid ShellLinkType = new("00021401-0000-0000-C000-000000000046");
    private static readonly PropertyKey AppIdKey = new(new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

    public static void SetAppIdentity() => Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(AppId));

    public static void Install(string executable, string args, string cwd, bool enableStartup)
    {
        string programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), LinkName);
        SetLink(programs, true, executable, args, cwd);
        if (enableStartup)
        {
            string startup = StartupPath();
            // Keep Windows' approval for an existing link. Also preserve a
            // previously disabled legacy login item if no link was created yet.
            if (File.Exists(startup) || !WindowsDisabled())
                SetLink(startup, true, executable, args, cwd);
        }
    }

    public static void SetEnabled(bool enabled, string executable, string args, string cwd) =>
        SetLink(StartupPath(), enabled, executable, args, cwd);

    public static bool WindowsDisabled()
    {
        if (ReadDisabled("StartupFolder", LinkName) is bool disabled) return disabled;
        if (File.Exists(StartupPath())) return false;
        return ReadDisabled("Run", AppId) ?? false;
    }

    internal static void SetLink(string path, bool enabled, string executable, string args, string cwd)
    {
        if (File.Exists(path))
        {
            var previous = ReadLink(path);
            if (!string.Equals(previous.Identity, AppId, StringComparison.Ordinal))
                throw new IOException("同名的启动快捷方式已被其他应用使用。");
            if (!enabled)
            {
                File.Delete(path);
                return;
            }
            if (string.Equals(previous.Target, executable, StringComparison.OrdinalIgnoreCase)
                && previous.Args == args && string.Equals(previous.Cwd, cwd, StringComparison.OrdinalIgnoreCase))
                return;
        }
        if (!enabled) return;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var link = CreateLink();
        try
        {
            link.SetPath(executable);
            link.SetArguments(args);
            link.SetWorkingDirectory(cwd);
            link.SetDescription("学习桌面");
            var properties = (IPropertyStore)link;
            var id = new PropVariant { Type = 31, String = Marshal.StringToCoTaskMemUni(AppId) };
            try
            {
                properties.SetValue(in AppIdKey, in id);
                properties.Commit();
            }
            finally { _ = PropVariantClear(ref id); }
            ((IPersistFile)link).Save(temporary, false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static (string Target, string Args, string Cwd, string? Identity) ReadLink(string path)
    {
        var link = CreateLink();
        try
        {
            ((IPersistFile)link).Load(path, 0);
            var properties = (IPropertyStore)link;
            properties.GetValue(in AppIdKey, out var id);
            try
            {
                var target = new StringBuilder(32768);
                var args = new StringBuilder(32768);
                var cwd = new StringBuilder(32768);
                link.GetPath(target, target.Capacity, 0, 4);
                link.GetArguments(args, args.Capacity);
                link.GetWorkingDirectory(cwd, cwd.Capacity);
                return (target.ToString(), args.ToString(), cwd.ToString(),
                    id.Type == 31 ? Marshal.PtrToStringUni(id.String) : null);
            }
            finally { _ = PropVariantClear(ref id); }
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    private static IShellLink CreateLink() =>
        (IShellLink)Activator.CreateInstance(Type.GetTypeFromCLSID(ShellLinkType, throwOnError: true)!)!;

    private static string StartupPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), LinkName);

    private static bool? ReadDisabled(string category, string name)
    {
        using var approval = Registry.CurrentUser.OpenSubKey(ApprovalKey + category);
        return approval?.GetValue(name) is byte[] { Length: >= 1 } state ? (state[0] & 1) != 0 : null;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int PropVariantClear(ref PropVariant value);

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public nint String;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly struct PropertyKey(Guid format, uint id)
    {
        private readonly Guid format = format;
        private readonly uint id = id;
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(in PropertyKey key, out PropVariant value);
        void SetValue(in PropertyKey key, in PropVariant value);
        void Commit();
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLink
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int capacity, nint findData, uint flags);
        void GetIDList(out nint itemList);
        void SetIDList(nint itemList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int capacity);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder workingDirectory, int capacity);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string workingDirectory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int capacity);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCommand);
        void SetShowCmd(int showCommand);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int capacity, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, uint reserved);
        void Resolve(nint window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}
