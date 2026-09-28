using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace WallhackTerminal.Services;

public enum OverlayPosition { BottomCenter, TopCenter, BottomRight, TopRight, BottomLeft, TopLeft }

public sealed class AppSettings
{
    public const int PresetCount = 7;

    public bool WindowsNotifications { get; set; } = true;
    public bool OverlayNotifications { get; set; }
    public OverlayPosition OverlayPosition { get; set; } = OverlayPosition.BottomCenter;
    public bool NotifyDpi { get; set; } = true;
    public bool NotifyPollRate { get; set; } = true;
    public bool NotifyConnection { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool GrainEffect { get; set; } = true;
    public bool LoggerExpanded { get; set; } = true;

    public string[] DpiPresetLabels { get; set; } = new string[PresetCount];
    public string[] PollPresetLabels { get; set; } = new string[PresetCount];

    public static string Directory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WallhackTerminal");

    static string FilePath => Path.Combine(Directory, "settings.json");

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        AppSettings settings;
        try
        {
            settings = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new()
                : new();
        }
        catch (Exception ex)
        {
            Log.Error("settings load failed", ex);
            settings = new();
        }
        settings.DpiPresetLabels = Normalize(settings.DpiPresetLabels);
        settings.PollPresetLabels = Normalize(settings.PollPresetLabels);
        return settings;
    }

    static string[] Normalize(string[]? labels)
    {
        var result = new string[PresetCount];
        for (int i = 0; i < PresetCount; i++) result[i] = labels is not null && i < labels.Length ? labels[i]?.Trim() ?? "" : "";
        return result;
    }

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex)
        {
            Log.Error("settings save failed", ex);
        }
    }
}

public static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "WallhackTerminal";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --tray");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

public static class Log
{
    static readonly object Gate = new();
    static string FilePath => Path.Combine(AppSettings.Directory, "log.txt");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}: {ex}");

    static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(AppSettings.Directory);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 512 * 1024) info.Delete();
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }
}
