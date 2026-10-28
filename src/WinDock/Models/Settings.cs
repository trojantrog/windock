using System.Text.Json;

namespace WinDock.Models;

public sealed class Settings
{
    public bool AutoSwitch { get; set; } = true;
    public bool Notify { get; set; } = true;
    public string Language { get; set; } = System.Globalization.CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en";
    public string? ActiveId { get; set; }
    public List<PinItem> AlwaysPinned { get; set; } = [];
    public DockSettings Dock { get; set; } = new();
    public bool SyncTaskbar { get; set; }
    public Dictionary<string, DateTime> FolderSeen { get; set; } = [];
    public bool CheckUpdates { get; set; } = true;
    public string? UpdateSeen { get; set; }
    public bool TourSeen { get; set; }
    public DesktopSettings Desktop { get; set; } = new();

    public static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinDock");

    static string FilePath => Path.Combine(Dir, "settings.json");

    public static bool Exists => File.Exists(FilePath);

    public static Settings Load()
    {
        try { return Parse(File.ReadAllText(FilePath)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(); }
    }

    public static Settings Parse(string json)
    {
        try
        {
            var settings = JsonSerializer.Deserialize<Settings>(json, Json.Options) ?? new();
            settings.Dock ??= new();
            settings.Desktop ??= new();
            settings.Dock.Clamp();
            return settings;
        }
        catch (JsonException) { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json.Options));
    }
}

public sealed class DockSettings
{
    public const string LensNone = "none";
    public const string LensScale = "scale";
    public const string LensMagnify = "magnify";
    public const string ThemeDark = "dark";
    public const string ThemeLight = "light";

    public bool Enabled { get; set; } = true;
    public bool ReplaceTaskbar { get; set; }
    public bool AutoHide { get; set; }
    public bool Reserve { get; set; } = true;
    public bool FollowWindows { get; set; }
    public string Lens { get; set; } = LensMagnify;
    public double Magnification { get; set; } = 1.75;
    public double LensWidth { get; set; } = 2.5;
    public double IconSize { get; set; } = 46;
    public double IconGap { get; set; } = 6;
    public double PlateOpacity { get; set; } = 0.9;
    public double PlatePadding { get; set; } = 8;
    public double PlateCornerRadius { get; set; } = 14;
    public double BottomMargin { get; set; } = 6;
    public string RunningOrder { get; set; } = "opened";
    public string RunningPosition { get; set; } = "after-pins";
    public string IndicatorStyle { get; set; } = "line";
    public double IndicatorSize { get; set; } = 4;
    public bool UseAccentIndicator { get; set; } = true;
    public bool ShowBadges { get; set; } = true;
    public bool ShowAppNames { get; set; } = true;
    public bool ShowBackgroundApps { get; set; } = true;
    public string Theme { get; set; } = ThemeDark;
    public bool AllScreens { get; set; } = true;
    public bool CompactWidgets { get; set; } = true;
    public bool PerScreen { get; set; }
    public Dictionary<string, string> ScreenEnvironments { get; set; } = [];
    public string? Screen { get; set; }
    public bool IconAnimation { get; set; }
    public double AnimationStrength { get; set; } = 0.32;
    public bool WindowMotion { get; set; } = true;
    public int WindowMotionDuration { get; set; } = 320;
    public bool WindowOpenAnimation { get; set; } = true;
    public int WindowOpenDuration { get; set; } = 240;
    public bool NativeWindowAnimations { get; set; }
    public bool WindowCloseAnimation { get; set; } = true;
    public int WindowCloseDuration { get; set; } = 180;
    public bool WindowRestoreAnimation { get; set; } = true;
    public int WindowRestoreDuration { get; set; } = 240;
    public bool WindowHideAnimation { get; set; } = true;
    public double WindowCloseDistance { get; set; } = 36;
    public double WindowCloseScale { get; set; } = 0.92;
    public double WindowFadeHold { get; set; } = 0.85;
    public string WindowMotionCurve { get; set; } = "smooth";
    public bool SearchEnabled { get; set; } = true;
    public bool OrganizerEnabled { get; set; } = true;
    public bool SystemTrayEnabled { get; set; }
    public bool TrayPinned { get; set; } = true;
    public double TrayIconSize { get; set; } = 22;
    public int TrayColumns { get; set; } = 5;
    public bool TrayShowNames { get; set; }
    public string TraySort { get; set; } = "system";
    public List<string> TrayPinnedIds { get; set; } = [];
    public List<string> TrayOrder { get; set; } = [];

    public void Clamp()
    {
        Magnification = Math.Clamp(Magnification, 1.1, 2.4);
        LensWidth = Math.Clamp(LensWidth, 1, 4.5);
        IconSize = Math.Clamp(IconSize, 24, 96);
        IconGap = Math.Clamp(IconGap, 0, 24);
        PlateOpacity = Math.Clamp(PlateOpacity, 0, 1);
        PlatePadding = Math.Clamp(PlatePadding, 0, 24);
        PlateCornerRadius = Math.Clamp(PlateCornerRadius, 0, 40);
        BottomMargin = Math.Clamp(BottomMargin, 0, 40);
        IndicatorSize = Math.Clamp(IndicatorSize, 2, 8);
        if (RunningOrder is not ("opened" or "recent" or "name")) RunningOrder = "opened";
        if (RunningPosition is not ("after-pins" or "before-pins")) RunningPosition = "after-pins";
        if (IndicatorStyle is not ("line" or "dot" or "square" or "none")) IndicatorStyle = "line";
        AnimationStrength = Math.Clamp(AnimationStrength, 0.2, 1.3);
        WindowMotionDuration = Math.Clamp(WindowMotionDuration, 120, 600);
        WindowOpenDuration = Math.Clamp(WindowOpenDuration, 120, 600);
        WindowCloseDuration = Math.Clamp(WindowCloseDuration, 120, 600);
        WindowRestoreDuration = Math.Clamp(WindowRestoreDuration, 120, 600);
        WindowCloseDistance = double.IsFinite(WindowCloseDistance) ? Math.Clamp(WindowCloseDistance, 0, 120) : 36;
        WindowCloseScale = double.IsFinite(WindowCloseScale) ? Math.Clamp(WindowCloseScale, .75, 1) : .92;
        WindowFadeHold = double.IsFinite(WindowFadeHold) ? Math.Clamp(WindowFadeHold, .5, .95) : .85;
        if (WindowMotionCurve is not ("smooth" or "quick")) WindowMotionCurve = "smooth";
        TrayIconSize = Math.Clamp(TrayIconSize, 16, 32);
        TrayColumns = Math.Clamp(TrayColumns, 3, 8);
        if (TraySort is not ("system" or "name")) TraySort = "system";
        TrayPinnedIds = (TrayPinnedIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).Take(512).ToList();
        TrayOrder = (TrayOrder ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).Take(512).ToList();
        if (Lens is not (LensNone or LensScale or LensMagnify)) Lens = LensMagnify;
        if (DockTheme.All.All(t => t.Id != Theme)) Theme = ThemeDark;
    }
}

public sealed class DesktopSettings
{
    public string? PecoFencePath { get; set; }
    public string SearchHotkey { get; set; } = "Alt+Space";
    public string EverythingInstance { get; set; } = "";
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
}
