using System.Runtime.CompilerServices;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using WinDock.Models;
using WinDock.Services;

namespace WinDock.ViewModels;

public sealed class DockOptions : ObservableObject
{
    readonly Settings settings;
    readonly DispatcherTimer save = new() { Interval = TimeSpan.FromMilliseconds(400) };

    public DockOptions(Settings settings)
    {
        this.settings = settings;
        save.Tick += (_, _) =>
        {
            save.Stop();
            settings.Save();
        };
    }

    DockSettings D => settings.Dock;

    public event Action<string>? Changed;

    public bool Enabled { get => D.Enabled; set => Set(() => D.Enabled = value); }
    public bool ReplaceTaskbar { get => D.ReplaceTaskbar; set => Set(() => D.ReplaceTaskbar = value); }
    public bool AutoHide { get => D.AutoHide; set => Set(() => D.AutoHide = value, nameof(CanReserve)); }
    public bool Reserve { get => D.Reserve; set => Set(() => D.Reserve = value); }
    public bool FollowWindows { get => D.FollowWindows; set => Set(() => D.FollowWindows = value); }
    public bool SyncTaskbar { get => settings.SyncTaskbar; set => Set(() => settings.SyncTaskbar = value); }
    public double Magnification { get => D.Magnification; set => Set(() => D.Magnification = Math.Round(value, 2)); }
    public double LensWidth { get => D.LensWidth; set => Set(() => D.LensWidth = Math.Round(value, 2)); }
    public double IconSize { get => D.IconSize; set => Set(() => D.IconSize = Math.Round(value)); }
    public double IconGap { get => D.IconGap; set => Set(() => D.IconGap = Math.Round(value)); }
    public double PlateOpacity { get => D.PlateOpacity; set => Set(() => D.PlateOpacity = Math.Round(value, 2)); }
    public double PlatePadding { get => D.PlatePadding; set => Set(() => D.PlatePadding = Math.Round(value)); }
    public double PlateCornerRadius { get => D.PlateCornerRadius; set => Set(() => D.PlateCornerRadius = Math.Round(value)); }
    public double BottomMargin { get => D.BottomMargin; set => Set(() => D.BottomMargin = Math.Round(value)); }
    public string RunningOrder { get => D.RunningOrder; set => Set(() => D.RunningOrder = value); }
    public string RunningPosition { get => D.RunningPosition; set => Set(() => D.RunningPosition = value); }
    public string IndicatorStyle { get => D.IndicatorStyle; set => Set(() => D.IndicatorStyle = value); }
    public double IndicatorSize { get => D.IndicatorSize; set => Set(() => D.IndicatorSize = Math.Round(value)); }
    public bool UseAccentIndicator { get => D.UseAccentIndicator; set => Set(() => D.UseAccentIndicator = value); }
    public bool ShowBadges { get => D.ShowBadges; set => Set(() => D.ShowBadges = value); }
    public bool ShowAppNames { get => D.ShowAppNames; set => Set(() => D.ShowAppNames = value); }
    public bool ShowBackgroundApps { get => D.ShowBackgroundApps; set => Set(() => D.ShowBackgroundApps = value); }
    public string Lens { get => D.Lens; set => Set(() => D.Lens = value, nameof(LensNone), nameof(LensScale), nameof(LensMagnify)); }
    public string Theme { get => D.Theme; set => Set(() => D.Theme = value); }
    public string? Screen { get => D.Screen; set => Set(() => D.Screen = value); }
    public bool AllScreens { get => D.AllScreens; set => Set(() => D.AllScreens = value); }
    public bool CompactWidgets { get => D.CompactWidgets; set => Set(() => D.CompactWidgets = value); }
    public bool PerScreen { get => D.PerScreen; set => Set(() => D.PerScreen = value); }
    public bool CheckUpdates { get => settings.CheckUpdates; set => Set(() => settings.CheckUpdates = value); }
    public bool IconAnimation { get => D.IconAnimation; set => Set(() => D.IconAnimation = value); }
    public double AnimationStrength { get => D.AnimationStrength; set => Set(() => D.AnimationStrength = Math.Round(value, 2)); }
    public bool WindowMotion { get => D.WindowMotion; set => Set(() => D.WindowMotion = value); }
    public int WindowMotionDuration { get => D.WindowMotionDuration; set => Set(() => D.WindowMotionDuration = value); }
    public bool WindowOpenAnimation { get => D.WindowOpenAnimation; set => Set(() => D.WindowOpenAnimation = value); }
    public int WindowOpenDuration { get => D.WindowOpenDuration; set => Set(() => D.WindowOpenDuration = value); }
    public bool NativeWindowAnimations { get => D.NativeWindowAnimations; set => Set(() => D.NativeWindowAnimations = value); }
    public bool WindowCloseAnimation { get => D.WindowCloseAnimation; set => Set(() => D.WindowCloseAnimation = value, nameof(WindowCloseParametersEnabled)); }
    public int WindowCloseDuration { get => D.WindowCloseDuration; set => Set(() => D.WindowCloseDuration = value); }
    public bool WindowRestoreAnimation { get => D.WindowRestoreAnimation; set => Set(() => D.WindowRestoreAnimation = value); }
    public int WindowRestoreDuration { get => D.WindowRestoreDuration; set => Set(() => D.WindowRestoreDuration = value); }
    public bool WindowHideAnimation { get => D.WindowHideAnimation; set => Set(() => D.WindowHideAnimation = value, nameof(WindowCloseParametersEnabled)); }
    public bool WindowCloseParametersEnabled => WindowCloseAnimation || WindowHideAnimation;
    public double WindowCloseDistance { get => D.WindowCloseDistance; set => Set(() => D.WindowCloseDistance = Math.Round(value)); }
    public double WindowCloseScale { get => D.WindowCloseScale; set => Set(() => D.WindowCloseScale = Math.Round(value, 2)); }
    public double WindowFadeHold { get => D.WindowFadeHold; set => Set(() => D.WindowFadeHold = Math.Round(value, 2)); }
    public string WindowMotionCurve { get => D.WindowMotionCurve; set => Set(() => D.WindowMotionCurve = value); }
    string windowAnimationStatus = "";
    public string WindowAnimationStatus { get => windowAnimationStatus; set => SetProperty(ref windowAnimationStatus, value); }
    bool windowAnimationLoginEnabled;
    public bool WindowAnimationLoginEnabled { get => windowAnimationLoginEnabled; set => SetProperty(ref windowAnimationLoginEnabled, value); }
    bool windowAnimationControlsEnabled;
    public bool WindowAnimationControlsEnabled { get => windowAnimationControlsEnabled; set => SetProperty(ref windowAnimationControlsEnabled, value); }
    public bool SearchEnabled { get => D.SearchEnabled; set => Set(() => D.SearchEnabled = value); }
    public bool OrganizerEnabled { get => D.OrganizerEnabled; set => Set(() => D.OrganizerEnabled = value); }
    public bool SystemTrayEnabled { get => D.SystemTrayEnabled; set => Set(() => D.SystemTrayEnabled = value); }
    public bool TrayPinned { get => D.TrayPinned; set => Set(() => D.TrayPinned = value); }
    public double TrayIconSize { get => D.TrayIconSize; set => Set(() => D.TrayIconSize = Math.Round(value)); }
    public int TrayColumns { get => D.TrayColumns; set => Set(() => D.TrayColumns = value); }
    public bool TrayShowNames { get => D.TrayShowNames; set => Set(() => D.TrayShowNames = value); }
    public bool TrayMenuPlacement { get => D.TrayMenuPlacement; set => Set(() => D.TrayMenuPlacement = value); }
    public string TraySort { get => D.TraySort; set => Set(() => D.TraySort = value); }
    public IReadOnlyList<string> TrayPinnedIds => D.TrayPinnedIds;
    public IReadOnlyList<string> TrayOrder => D.TrayOrder;
    public void SetTrayPinned(string id, bool pinned) => Set(() =>
    {
        D.TrayPinnedIds.RemoveAll(key => key.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (pinned) D.TrayPinnedIds.Add(id);
    }, name: nameof(TrayPinnedIds));
    public void SetTrayOrder(IEnumerable<string> ids) => Set(() => D.TrayOrder = ids.ToList(), name: nameof(TrayOrder));

    string? newVersion;
    string searchShortcutStatus = "";
    public string SearchShortcutStatus { get => searchShortcutStatus; set { searchShortcutStatus = value; OnPropertyChanged(); } }

    public string? NewVersion
    {
        get => newVersion;
        set
        {
            newVersion = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasUpdate));
            OnPropertyChanged(nameof(UpdateText));
        }
    }

    public bool HasUpdate => NewVersion is not null;
    public string UpdateText => NewVersion is null ? "" : Loc.F("updateDownload", NewVersion);
    public Dictionary<string, string> ScreenEnvironments => D.ScreenEnvironments;

    public void SetScreenEnvironment(string screen, string id) => Set(() => D.ScreenEnvironments[screen] = id, name: nameof(ScreenEnvironments));

    public bool CanReserve => !AutoHide;

    public void Retemplate() => Changed?.Invoke(nameof(CompactWidgets));

    public bool StartWithWindows
    {
        get => Autostart.Enabled;
        set
        {
            Autostart.Enabled = value;
            OnPropertyChanged();
        }
    }

    public bool LensNone { get => Lens == DockSettings.LensNone; set { if (value) Lens = DockSettings.LensNone; else OnPropertyChanged(); } }
    public bool LensScale { get => Lens == DockSettings.LensScale; set { if (value) Lens = DockSettings.LensScale; else OnPropertyChanged(); } }
    public bool LensMagnify { get => Lens == DockSettings.LensMagnify; set { if (value) Lens = DockSettings.LensMagnify; else OnPropertyChanged(); } }

    void Set(Action apply, string also1 = "", string also2 = "", string also3 = "", [CallerMemberName] string name = "")
    {
        apply();
        D.Clamp();
        save.Stop();
        save.Start();
        OnPropertyChanged(name);
        foreach (var other in new[] { also1, also2, also3 }.Where(o => o.Length > 0)) OnPropertyChanged(other);
        Changed?.Invoke(name);
    }

    public void Flush()
    {
        if (!save.IsEnabled) return;
        save.Stop();
        settings.Save();
    }
}
