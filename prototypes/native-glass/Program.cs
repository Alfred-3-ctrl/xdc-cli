namespace Xdc.NativeGlass;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        if (args.Length > 0 && args[0] == "--backdrop")
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            return BackdropWorker.Run(args);
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            MessageBox.Show("此原型需要 Windows 11 22H2 或以上版本。", "原生玻璃原型");
            return 1;
        }

        using var form = new TestBackdropForm();
        try
        {
            using var glass = new GlassWindow(form.Handle);
            form.RadiusChanged += glass.SetRadius;
            form.EffectChanged += glass.SetEnabled;
            form.GlassBoundsChanged += glass.Place;
            form.SetStatus("正在加载系统模糊效果…");
            using var effectMonitor = new System.Windows.Forms.Timer { Interval = 150 };
            effectMonitor.Tick += (_, _) =>
            {
                string? status = glass.ReadEffectStatus();
                if (status is null) return;
                effectMonitor.Stop();
                form.SetStatus(status);
            };
            effectMonitor.Start();
            Application.Run(form);
            return 0;
        }
        catch (Exception error)
        {
            MessageBox.Show(
                $"无法创建原生玻璃。\r\n\r\n{error.Message}\r\nHRESULT 0x{error.HResult:X8}",
                "原生玻璃原型", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
