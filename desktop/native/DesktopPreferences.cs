using System.Runtime.InteropServices;
using System.Text.Json;

namespace Xdc.NativeDesktop;

internal sealed record DesktopLook
{
    public static DesktopLook Default { get; } = new();
    public string Scheme { get; init; } = "dark";
    public string Accent { get; init; } = "#0a5152";
    public int Transparency { get; init; } = 74;
    public int Scale { get; init; } = 92;
    public string Density { get; init; } = "regular";
    public string NavStyle { get; init; } = "both";
    public string NavAlign { get; init; } = "center";
    public int Radius { get; init; } = 30;
    public bool Blur { get; init; } = true;

    public DesktopLook Normalize() => this with
    {
        Scheme = Scheme is "system" or "light" or "dark" ? Scheme : Default.Scheme,
        Accent = Accent is { Length: 7 } && Accent[0] == '#' && Accent.Skip(1).All(Uri.IsHexDigit)
            ? Accent.ToLowerInvariant() : Default.Accent,
        Transparency = Math.Clamp(Transparency, 0, 95),
        Scale = Math.Clamp(Scale, 85, 125),
        Density = Density is "compact" or "regular" or "comfortable" ? Density : Default.Density,
        NavStyle = NavStyle is "both" or "text" or "icons" ? NavStyle : Default.NavStyle,
        NavAlign = NavAlign is "left" or "center" or "right" ? NavAlign : Default.NavAlign,
        Radius = Math.Clamp(Radius, 16, 36),
    };
}

internal sealed record DesktopPreferences
{
    private static readonly JsonSerializerOptions Format = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public const string DefaultShortcut = "Ctrl+Shift+L";
    public DesktopLook Look { get; init; } = DesktopLook.Default;
    public DesktopGeometry Geometry { get; init; } = DesktopGeometry.Default;
    public string Shortcut { get; init; } = DefaultShortcut;
    public bool Locked { get; init; } = true;
    public bool Startup { get; init; }
    public string DownloadDirectory { get; init; } = DefaultDownloadDirectory();

    public static DesktopPreferences Load(string profile)
    {
        var defaults = new DesktopPreferences();
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(profile, "preferences.json")));
            var values = document.RootElement;
            TryField(values, "look", out var appearance);
            TryField(values, "geometry", out var bounds);
            var look = new DesktopLook
            {
                Scheme = Text(appearance, "scheme", defaults.Look.Scheme),
                Accent = Text(appearance, "accent", defaults.Look.Accent),
                Transparency = Number(appearance, "transparency", defaults.Look.Transparency),
                Scale = Number(appearance, "scale", defaults.Look.Scale),
                Density = Text(appearance, "density", defaults.Look.Density),
                NavStyle = Text(appearance, "navStyle", defaults.Look.NavStyle),
                NavAlign = Text(appearance, "navAlign", defaults.Look.NavAlign),
                Radius = Number(appearance, "radius", defaults.Look.Radius),
                Blur = !TryField(appearance, "blur", out var blur) || blur.ValueKind != JsonValueKind.False,
            };
            var geometry = new DesktopGeometry(
                Number(bounds, "x", defaults.Geometry.X),
                Number(bounds, "y", defaults.Geometry.Y),
                Number(bounds, "width", defaults.Geometry.Width),
                Number(bounds, "height", defaults.Geometry.Height));
            string directory = Text(values, "downloadDirectory", defaults.DownloadDirectory);
            return defaults with
            {
                Look = look.Normalize(),
                Geometry = geometry.Normalize(),
                Shortcut = LayoutShortcut.Parse(Text(values, "shortcut", DefaultShortcut))?.Name ?? DefaultShortcut,
                Locked = !TryField(values, "locked", out var locked) || locked.ValueKind != JsonValueKind.False,
                Startup = TryField(values, "startup", out var startup) && startup.ValueKind == JsonValueKind.True,
                DownloadDirectory = Path.IsPathFullyQualified(directory) && !directory.Contains('\0')
                    ? directory : defaults.DownloadDirectory
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return defaults with { Geometry = defaults.Geometry.Normalize() };
        }
    }

    public void Save(string profile)
    {
        Directory.CreateDirectory(profile);
        string path = Path.Combine(profile, "preferences.json");
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
                JsonSerializer.Serialize(stream, this, Format);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static bool TryField(JsonElement node, string key, out JsonElement result)
    {
        result = default;
        return node.ValueKind == JsonValueKind.Object && node.TryGetProperty(key, out result);
    }

    private static string Text(JsonElement node, string key, string fallback) =>
        TryField(node, key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback : fallback;

    private static int Number(JsonElement node, string key, int fallback) =>
        TryField(node, key, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out double number) && double.IsFinite(number)
            ? (int)Math.Clamp(Math.Floor(number + .5), int.MinValue, int.MaxValue) : fallback;

    private static string DefaultDownloadDirectory()
    {
        var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        if (SHGetKnownFolderPath(in id, 0, 0, out nint result) == 0 && result != 0)
        {
            try
            {
                string? path = Marshal.PtrToStringUni(result);
                if (!string.IsNullOrEmpty(path)) return Path.Combine(path, "学习桌面");
            }
            finally { Marshal.FreeCoTaskMem(result); }
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "学习桌面");
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(in Guid id, uint flags, nint token, out nint path);
}
