using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Xdc.NativeDesktop;

internal sealed record NativeOptions(string Desktop, string Profile, bool Managed, string? Report)
{
    public static NativeOptions Parse(string[] args)
    {
        string? desktop = null, profile = null, report = null;
        for (int i = 0; i < args.Length; i += 2)
        {
            if (i + 1 == args.Length) throw new ArgumentException("启动参数缺少路径。");
            string value = Path.GetFullPath(args[i + 1]);
            switch (args[i])
            {
                case "--desktop" when desktop is null: desktop = value; break;
                case "--profile" when profile is null: profile = value; break;
                case "--report" when report is null: report = value; break;
                default: throw new ArgumentException("无法识别该启动参数。");
            }
        }

        desktop ??= Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        if (!Directory.Exists(Path.Combine(desktop, "native-service")))
            throw new DirectoryNotFoundException("找不到学习桌面的业务服务。请从项目的 desktop 目录启动。");

        string defaultProfile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "xdc-desktop");
        if (profile is not null && Path.GetFullPath(profile).TrimEnd(Path.DirectorySeparatorChar)
            .Equals(defaultProfile, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("隔离配置路径必须与日常桌面配置不同。");
        return new NativeOptions(desktop, profile ?? defaultProfile, profile is null, report);
    }
}

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        NativeOptions? options = null;
        try
        {
            options = NativeOptions.Parse(args);
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
                throw new PlatformNotSupportedException("学习桌面需要 Windows 11 22H2 或更新版本。");
            var key = SHA256.HashData(Encoding.UTF8.GetBytes(options.Profile.ToUpperInvariant()));
            using var singleInstance = new Mutex(true, @"Local\Xdc.NativeDesktop." + Convert.ToHexString(key), out bool acquired);
            if (!acquired) return 0;

            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            if (options.Managed) DesktopStartup.SetAppIdentity();
            using var window = new LearningWindow(options);
            Application.Run(window);
            singleInstance.ReleaseMutex();
            return 0;
        }
        catch (Exception error)
        {
            if (options?.Report is { } path)
            {
                try { File.WriteAllText(path, JsonSerializer.Serialize(new { ready = false, error = error.Message })); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            else
            {
                MessageBox.Show(error.Message, "无法启动学习桌面", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return 1;
        }
    }
}
