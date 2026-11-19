using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using System.Windows;
using System.Windows.Data;
using System.Globalization;

namespace WristMusic;

public sealed class PageConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var match = string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);
        return targetType == typeof(Visibility) ? match ? Visibility.Visible : Visibility.Collapsed : match;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? parameter : Binding.DoNothing;
}

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Notify(name); return true;
    }
    public void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class ActionCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => execute(parameter);
}

public sealed class AppEntry(string id, string name, string mark, string color, string[] aliases) : Observable
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Mark { get; } = mark;
    public string Color { get; } = color;
    public string[] Aliases { get; } = aliases;
    private string _path = "";
    public string ExePath { get => _path; set { if (Set(ref _path, value)) Notify(nameof(PathLabel)); } }
    internal Windows.ApplicationModel.Package? StorePackage { get; set; }
    public string PathLabel => StorePackage is not null && string.IsNullOrEmpty(ExePath) ? "Microsoft Store 版" : string.IsNullOrEmpty(ExePath) ? "未找到安装路径 · 可手动选择" : ExePath;
    private bool _running;
    public bool Running { get => _running; set { if (Set(ref _running, value)) Notify(nameof(StateLabel)); } }
    public string StateLabel => Running ? "运行中" : (StorePackage is not null || File.Exists(ExePath)) ? "已配置" : "未配置";
    public bool Matches(string source) => Aliases.Any(a => source.Contains(a, StringComparison.OrdinalIgnoreCase)) ||
        (!string.IsNullOrEmpty(ExePath) && source.Contains(Path.GetFileNameWithoutExtension(ExePath), StringComparison.OrdinalIgnoreCase));
    public override string ToString() => Name;
}

public sealed class UserSettings
{
    public string SelectedApp { get; set; } = "auto";
    public Dictionary<string, string> AppPaths { get; set; } = [];
    public bool RightWrist { get; set; }
    public double Scale { get; set; } = 100;
    public double Opacity { get; set; } = 95;
    public double OffsetX { get; set; } = 0;
    public double OffsetY { get; set; } = 8;
    public double OffsetZ { get; set; } = 6;
    public double Tilt { get; set; } = -75;
    public bool RaiseToShow { get; set; }
    public bool DoubleBToOpen { get; set; } = true;
    public int VisibilitySettingsVersion { get; set; } = 1;
    public bool OverlayEnabled { get; set; } = true;
    public bool GlobalFallback { get; set; }
    public bool Hotkeys { get; set; } = true;
    public bool DarkMode { get; set; }
    public bool FollowSteamVr { get; set; }
    public bool ForceClosePlayer { get; set; }
    public bool OpenLastPlayer { get; set; }
    public bool AutoUpdate { get; set; } = true;
    public string? LastPlayerId { get; set; }
    public bool LaunchSteamVr { get; set; }
    public bool ExperimentalFeaturesEnabled { get; set; }
    public bool ShowListeningTime { get; set; }
    public double TotalListeningSeconds { get; set; }
    public void Normalize()
    {
        Scale = Clamp(Scale, 50, 160, 100); Opacity = Clamp(Opacity, 30, 100, 95);
        OffsetX = Clamp(OffsetX, -30, 30, 0); OffsetY = Clamp(OffsetY, -30, 30, 8);
        OffsetZ = Clamp(OffsetZ, -30, 30, 6); Tilt = Clamp(Tilt, -180, 180, -75);
        AppPaths ??= []; SelectedApp ??= "auto";
        TotalListeningSeconds = Clamp(TotalListeningSeconds, 0, 36500d * 86400, 0);
    }
    private static double Clamp(double value, double min, double max, double fallback) => double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}

public sealed class SettingsStore(string? path = null)
{
    public string FilePath { get; } = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WristMusic", "settings.json");
    public string? LoadError { get; private set; }
    public UserSettings Load()
    {
        try
        {
            var settings = new UserSettings();
            if (File.Exists(FilePath))
            {
                using var json = JsonDocument.Parse(File.ReadAllText(FilePath));
                settings = json.RootElement.Deserialize<UserSettings>() ?? new();
                // Earlier releases persisted the old default of true. Migrate it once;
                // subsequent explicit choices are preserved by the revision marker.
                if (!json.RootElement.TryGetProperty(nameof(UserSettings.VisibilitySettingsVersion), out var revision) || revision.GetInt32() < 1)
                { settings.RaiseToShow = false; settings.VisibilitySettingsVersion = 1; }
            }
            settings.Normalize(); return settings;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        { LoadError = "设置无法读取，已使用默认值：" + e.Message; return new(); }
    }
    public void Save(UserSettings settings)
    {
        settings.Normalize(); Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, FilePath, true);
    }
}

public static class MediaMath
{
    public static double Position(double position, double elapsed, double min, double max, bool playing) =>
        Math.Clamp(position + (playing ? Math.Max(0, elapsed) : 0), min, Math.Max(min, max));
    public static string Time(double seconds) => seconds >= 3600 ? TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"h\:mm\:ss") : TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"mm\:ss");
}
