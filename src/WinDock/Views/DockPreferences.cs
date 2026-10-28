using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using WinDock.Services;
using WinDock.ViewModels;

namespace WinDock.Views;

public sealed class DockPreferences : StackPanel
{
    sealed record Choice(string Id, string Name);
    public DockPreferences()
    {
        Loaded += (_, _) => { Loc.Instance.PropertyChanged += LanguageChanged; Build(); };
        Unloaded += (_, _) => Loc.Instance.PropertyChanged -= LanguageChanged;
    }

    void LanguageChanged(object? sender, PropertyChangedEventArgs e) => Build();
    Brush Ink => (Brush)FindResource("Ink");
    Brush Muted => (Brush)FindResource("Muted");
    Style StyleOf(string key) => (Style)FindResource(key);

    void Build()
    {
        Children.Clear();
        var appearance = Group("appearanceSettings");
        Number(appearance, "iconSize", nameof(DockOptions.IconSize), 24, 96, 1);
        Number(appearance, "iconGap", nameof(DockOptions.IconGap), 0, 24, 1);
        Number(appearance, "platePadding", nameof(DockOptions.PlatePadding), 0, 24, 1);
        Number(appearance, "plateCornerRadius", nameof(DockOptions.PlateCornerRadius), 0, 40, 1);
        Number(appearance, "bottomMargin", nameof(DockOptions.BottomMargin), 0, 40, 1);
        Number(appearance, "plateOpacity", nameof(DockOptions.PlateOpacity), 0, 1, .01, "{0:P0}");

        var apps = Group("appDisplaySettings");
        Select(apps, "runningOrder", nameof(DockOptions.RunningOrder), ("opened", "orderStable"), ("recent", "orderRecent"), ("name", "orderName"));
        Select(apps, "runningPosition", nameof(DockOptions.RunningPosition), ("after-pins", "afterPins"), ("before-pins", "beforePins"));
        Select(apps, "indicatorStyle", nameof(DockOptions.IndicatorStyle), ("line", "indicatorLine"), ("dot", "indicatorDot"), ("square", "indicatorSquare"), ("none", "indicatorNone"));
        Number(apps, "indicatorSize", nameof(DockOptions.IndicatorSize), 2, 8, 1);
        Toggle(apps, "useAccentIndicator", nameof(DockOptions.UseAccentIndicator));
        Toggle(apps, "showBadges", nameof(DockOptions.ShowBadges));
        Toggle(apps, "showAppNames", nameof(DockOptions.ShowAppNames));
        Toggle(apps, "showBackgroundApps", nameof(DockOptions.ShowBackgroundApps));

        var motion = Group("animationTitle");
        Toggle(motion, "nativeWindowAnimations", nameof(DockOptions.NativeWindowAnimations));
        var login = new ToggleButton { Content = Loc.T("nativeMotionLogin"), Style = StyleOf("SwitchToggle"),
            Margin = new Thickness(0, 0, 0, 12) };
        login.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(DockOptions.WindowAnimationLoginEnabled)) { Mode = BindingMode.OneWay });
        login.SetBinding(IsEnabledProperty, new Binding(nameof(DockOptions.WindowAnimationControlsEnabled)));
        login.Click += async (_, _) =>
        {
            var enabled = login.IsChecked == true;
            try { await NativeWindowMotion.Shared.SetLoginEnabledAsync(enabled); }
            finally
            {
                login.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateTarget();
            }
        };
        motion.Children.Add(login);
        var activate = new Button { Content = Loc.T("nativeMotionActivate"), Style = StyleOf("ActionButton"), HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 0, 8) };
        activate.SetBinding(IsEnabledProperty, new Binding(nameof(DockOptions.WindowAnimationControlsEnabled)));
        activate.Click += async (_, _) => await NativeWindowMotion.Shared.ActivateAsync();
        motion.Children.Add(activate);
        var motionStatus = new TextBlock { Foreground = Muted, TextWrapping = TextWrapping.Wrap, FontSize = 11,
            Margin = new Thickness(0, 0, 0, 14) };
        motionStatus.SetBinding(TextBlock.TextProperty, new Binding(nameof(DockOptions.WindowAnimationStatus)));
        motion.Children.Add(motionStatus);
        Toggle(motion, "windowOpenAnimation", nameof(DockOptions.WindowOpenAnimation));
        Number(motion, "windowOpenDuration", nameof(DockOptions.WindowOpenDuration), 120, 600, 20, "{0} ms", nameof(DockOptions.WindowOpenAnimation));
        Toggle(motion, "windowCloseAnimation", nameof(DockOptions.WindowCloseAnimation));
        Number(motion, "windowCloseDuration", nameof(DockOptions.WindowCloseDuration), 120, 600, 20, "{0} ms", nameof(DockOptions.WindowCloseParametersEnabled));
        Toggle(motion, "windowMotion", nameof(DockOptions.WindowMotion));
        Number(motion, "windowMotionDuration", nameof(DockOptions.WindowMotionDuration), 120, 600, 20, "{0} ms", nameof(DockOptions.WindowMotion));
        Toggle(motion, "windowRestoreAnimation", nameof(DockOptions.WindowRestoreAnimation));
        Number(motion, "windowRestoreDuration", nameof(DockOptions.WindowRestoreDuration), 120, 600, 20, "{0} ms", nameof(DockOptions.WindowRestoreAnimation));
        Toggle(motion, "windowHideAnimation", nameof(DockOptions.WindowHideAnimation));
        Select(motion, "windowMotionCurve", nameof(DockOptions.WindowMotionCurve), ("smooth", "motionCurveSmooth"), ("quick", "motionCurveQuick"));
        Number(motion, "windowCloseDistance", nameof(DockOptions.WindowCloseDistance), 0, 120, 2, "{0} px", nameof(DockOptions.WindowCloseParametersEnabled));
        Number(motion, "windowCloseScale", nameof(DockOptions.WindowCloseScale), .75, 1, .01, "{0:P0}", nameof(DockOptions.WindowCloseParametersEnabled));
        Number(motion, "windowFadeHold", nameof(DockOptions.WindowFadeHold), .5, .95, .01, "{0:P0}");
        Toggle(motion, "iconAnimation", nameof(DockOptions.IconAnimation));
        Number(motion, "animationStrength", nameof(DockOptions.AnimationStrength), .2, 1.3, .01, "{0:0.00}×", nameof(DockOptions.IconAnimation));
        Select(motion, "hoverMotion", nameof(DockOptions.Lens), ("none", "indicatorNone"), ("scale", "lensScale"), ("magnify", "lensMagnify"));
        Number(motion, "magnification", nameof(DockOptions.Magnification), 1.1, 2.4, .01, "{0:0.00}×");
        Number(motion, "lensWidth", nameof(DockOptions.LensWidth), 1, 4.5, .1, "{0:0.0}");

        var tray = Group("systemTrayTitle");
        Toggle(tray, "systemTrayEnable", nameof(DockOptions.SystemTrayEnabled), "systemTrayConsent");
        Toggle(tray, "systemTrayPin", nameof(DockOptions.TrayPinned), enabledBy: nameof(DockOptions.SystemTrayEnabled));
        Number(tray, "trayIconSize", nameof(DockOptions.TrayIconSize), 16, 32, 1);
        Number(tray, "trayColumns", nameof(DockOptions.TrayColumns), 3, 8, 1);
        Toggle(tray, "trayShowNames", nameof(DockOptions.TrayShowNames));
        Toggle(tray, "trayMenuPlacement", nameof(DockOptions.TrayMenuPlacement), "trayMenuPlacementDesc");
        Select(tray, "traySort", nameof(DockOptions.TraySort), ("system", "orderCustom"), ("name", "orderName"));

        var behavior = Group("behaviorSettings");
        Toggle(behavior, "showDock", nameof(DockOptions.Enabled));
        Toggle(behavior, "trayStartup", nameof(DockOptions.StartWithWindows));
        Toggle(behavior, "replaceTaskbar", nameof(DockOptions.ReplaceTaskbar), "replaceTaskbarDesc");
        Toggle(behavior, "autoHide", nameof(DockOptions.AutoHide));
        Toggle(behavior, "reserve", nameof(DockOptions.Reserve), enabledBy: nameof(DockOptions.CanReserve));
        Toggle(behavior, "followWindows", nameof(DockOptions.FollowWindows), "followWindowsDesc");
        Toggle(behavior, "syncTaskbar", nameof(DockOptions.SyncTaskbar), "syncTaskbarDesc");
        Toggle(behavior, "checkUpdates", nameof(DockOptions.CheckUpdates));
    }

    StackPanel Group(string title)
    {
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = Loc.T(title), FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = Ink, Margin = new Thickness(0, 0, 0, 16), FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI") });
        Children.Add(new Border { Child = body, Padding = new Thickness(18, 16, 18, 8), Margin = new Thickness(0, 0, 0, 12),
            CornerRadius = new CornerRadius(16), Background = (Brush)FindResource("Plate"), BorderBrush = (Brush)FindResource("Hairline"), BorderThickness = new Thickness(1) });
        return body;
    }

    void Toggle(Panel parent, string label, string property, string? tooltip = null, string? enabledBy = null)
    {
        var toggle = new ToggleButton { Content = Loc.T(label), Style = StyleOf("SwitchToggle"), Margin = new Thickness(0, 0, 0, 12) };
        toggle.SetBinding(ToggleButton.IsCheckedProperty, new Binding(property) { Mode = BindingMode.TwoWay });
        if (tooltip is not null) toggle.ToolTip = Loc.T(tooltip);
        if (enabledBy is not null) toggle.SetBinding(IsEnabledProperty, new Binding(enabledBy));
        parent.Children.Add(toggle);
    }

    Grid Row(Panel parent, string label)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
        row.Children.Add(new TextBlock { Text = Loc.T(label), Foreground = Ink, FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0) });
        parent.Children.Add(row); return row;
    }

    void Number(Panel parent, string label, string property, double min, double max, double tick, string format = "{0:0}", string? enabledBy = null)
    {
        var row = Row(parent, label);
        var slider = new Slider { Minimum = min, Maximum = max, TickFrequency = tick, IsSnapToTickEnabled = true,
            SmallChange = tick, LargeChange = tick * 5, Style = StyleOf("FlatSlider"), VerticalAlignment = VerticalAlignment.Center };
        slider.SetBinding(RangeBase.ValueProperty, new Binding(property) { Mode = BindingMode.TwoWay });
        System.Windows.Automation.AutomationProperties.SetName(slider, Loc.T(label));
        Grid.SetColumn(slider, 1); row.Children.Add(slider);
        var value = new TextBlock { Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
        value.SetBinding(TextBlock.TextProperty, new Binding(property) { StringFormat = format });
        Grid.SetColumn(value, 2); row.Children.Add(value);
        if (enabledBy is not null) row.SetBinding(IsEnabledProperty, new Binding(enabledBy));
    }

    void Select(Panel parent, string label, string property, params (string Id, string Label)[] values)
    {
        var row = Row(parent, label);
        var combo = new ComboBox { Style = StyleOf("FlatCombo"), ItemsSource = values.Select(v => new Choice(v.Id, Loc.T(v.Label))).ToList(),
            DisplayMemberPath = nameof(Choice.Name), SelectedValuePath = nameof(Choice.Id), MinWidth = 160, HorizontalAlignment = HorizontalAlignment.Left };
        combo.SetBinding(Selector.SelectedValueProperty, new Binding(property) { Mode = BindingMode.TwoWay });
        System.Windows.Automation.AutomationProperties.SetName(combo, Loc.T(label));
        Grid.SetColumn(combo, 1); Grid.SetColumnSpan(combo, 2); row.Children.Add(combo);
    }
}
