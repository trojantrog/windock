using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinDock.Views;

namespace WinDock.Tests;

public class LensVerticalLayoutTests
{
    [Theory]
    [InlineData(LensMode.None)]
    [InlineData(LensMode.Scale)]
    [InlineData(LensMode.Magnify)]
    public void Mixed_height_apps_system_tiles_and_separators_share_the_widget_center(LensMode mode) => InSta(() =>
    {
        var panel = CreatePanel(mode);
        var app = AddTile(panel, 30, 30, true);
        var system = AddTile(panel, 96, 30, false);
        var separator = AddTile(panel, 11, 30, false);
        var widget = AddTile(panel, 180, 64, false);
        Arrange(panel);

        var card = Bounds(widget, panel);
        var center = card.Top + card.Height / 2;
        foreach (var child in new[] { app, system, separator })
        {
            var bounds = Bounds(child, panel);
            Assert.Equal(center, bounds.Top + bounds.Height / 2, 8);
            Assert.Equal(17, bounds.Top - card.Top, 8);
            Assert.Equal(17, card.Bottom - bounds.Bottom, 8);
        }
        Assert.Equal(64 + panel.Inset.Top + panel.Inset.Bottom, panel.Bar.Height, 8);
        Assert.Equal(panel.Inset.Top, card.Top - panel.Bar.Top, 8);
        Assert.Equal(panel.Inset.Bottom, panel.Bar.Bottom - card.Bottom, 8);
    });

    [Theory]
    [InlineData(LensMode.Scale, 1.3)]
    [InlineData(LensMode.Magnify, 1.4)]
    public void Hover_grows_upward_from_the_centered_icon_bottom_and_keeps_nonlens_tiles_fixed(LensMode mode, double scale) => InSta(() =>
    {
        var panel = CreatePanel(mode);
        var apps = Enumerable.Range(0, 18).Select(_ => AddTile(panel, 30, 30, true)).ToArray();
        var separator = AddTile(panel, 11, 30, false);
        var system = AddTile(panel, 96, 30, false);
        var widget = AddTile(panel, 180, 64, false);
        Arrange(panel);

        var app = apps[9];
        var resting = Bounds(app, panel);
        var fixedTiles = new[] { separator, system, widget };
        var fixedBounds = fixedTiles.Select(child => Bounds(child, panel)).ToArray();
        var bar = panel.Bar;
        Hover(panel, resting.Left + resting.Width / 2);

        var enlarged = Bounds(app, panel);
        Assert.Equal(30 * scale, enlarged.Height, 8);
        Assert.Equal(30 * scale, enlarged.Width, 8);
        Assert.Equal(resting.Bottom, enlarged.Bottom, 8);
        Assert.Equal(resting.Top - 30 * (scale - 1), enlarged.Top, 8);
        Assert.Equal(Bounds(widget, panel).Bottom - 17, enlarged.Bottom, 8);
        Assert.Equal(bar, panel.Bar);
        for (var i = 0; i < fixedTiles.Length; i++) Assert.Equal(fixedBounds[i], Bounds(fixedTiles[i], panel));

        panel.Amplitude = 0;
        Rearrange(panel);
        Assert.Equal(resting, Bounds(app, panel));
    });

    [Theory]
    [InlineData(LensMode.None, 30)]
    [InlineData(LensMode.Scale, 30)]
    [InlineData(LensMode.Magnify, 30)]
    [InlineData(LensMode.None, 64)]
    [InlineData(LensMode.Scale, 64)]
    [InlineData(LensMode.Magnify, 64)]
    public void Equal_height_tiles_keep_the_original_bottom_baseline_during_hover(LensMode mode, double height) => InSta(() =>
    {
        var panel = CreatePanel(mode);
        var apps = Enumerable.Range(0, 18).Select(_ => AddTile(panel, 30, height, true)).ToArray();
        var widget = AddTile(panel, 180, height, false);
        Arrange(panel);
        var resting = Bounds(apps[9], panel);
        Hover(panel, resting.Left + resting.Width / 2);

        var bottom = panel.RenderSize.Height - panel.Inset.Bottom;
        foreach (var app in apps) Assert.Equal(bottom, Bounds(app, panel).Bottom, 8);
        Assert.Equal(bottom, Bounds(widget, panel).Bottom, 8);
        Assert.Equal(bottom - height - panel.Inset.Top, panel.Bar.Top, 8);
    });

    [Theory]
    [InlineData("line", 1, 30)]
    [InlineData("line", 2, 64)]
    [InlineData("dot", 1, 64)]
    [InlineData("dot", 2, 64)]
    [InlineData("square", 1, 64)]
    [InlineData("square", 2, 64)]
    public void Running_indicators_follow_the_icon_bottom_in_the_actual_drawing(string style, int kind, double widgetHeight) => InSta(() =>
    {
        var panel = CreatePanel(LensMode.Magnify);
        panel.IndicatorStyle = style;
        panel.IndicatorSize = 4;
        panel.IndicatorBrush = Brushes.Black;
        panel.FocusBrush = Brushes.Black;
        var apps = Enumerable.Range(0, 18).Select(_ => AddTile(panel, 30, 30, true)).ToArray();
        var app = apps[9];
        LensPanel.SetIndicator(app, kind);
        var widget = AddTile(panel, 180, widgetHeight, false);
        Arrange(panel);

        AssertIndicator(panel, app, widget, style, kind);
        var resting = Bounds(app, panel);
        Hover(panel, resting.Left + resting.Width / 2);
        AssertIndicator(panel, app, widget, style, kind);
    });

    static LensPanel CreatePanel(LensMode mode) => new()
    {
        Mode = mode,
        Gap = 11,
        Inset = new Thickness(16, 10, 16, 12),
        Magnification = 1.4,
        Radius = 110.7
    };

    static Border AddTile(LensPanel panel, double width, double height, bool lens)
    {
        var tile = new Border { Width = width, Height = height };
        LensPanel.SetLens(tile, lens);
        panel.Children.Add(tile);
        return tile;
    }

    static void Arrange(LensPanel panel)
    {
        panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, panel.DesiredSize.Width + 200, panel.DesiredSize.Height + 100));
        panel.UpdateLayout();
    }

    static void Hover(LensPanel panel, double pointer)
    {
        panel.Pointer = pointer;
        panel.Amplitude = 1;
        Rearrange(panel);
    }

    static void Rearrange(LensPanel panel)
    {
        panel.Arrange(new Rect(panel.RenderSize));
        panel.UpdateLayout();
    }

    static Rect Bounds(UIElement child, LensPanel panel) => child.TransformToAncestor(panel).TransformBounds(new Rect(child.RenderSize));

    static void AssertIndicator(LensPanel panel, UIElement app, UIElement widget, string style, int kind)
    {
        var drawing = VisualTreeHelper.GetDrawing(panel);
        Assert.NotNull(drawing);
        var indicator = Assert.Single(Geometry(drawing), d => d.Brush is SolidColorBrush brush && brush.Color == Colors.Black);
        var bounds = indicator.Geometry.Bounds;
        var icon = Bounds(app, panel);
        var card = Bounds(widget, panel);
        Assert.Equal(icon.Left + icon.Width / 2, bounds.Left + bounds.Width / 2, 8);
        Assert.Equal(icon.Bottom + panel.Inset.Bottom / 2, bounds.Top + bounds.Height / 2, 8);
        Assert.Equal(card.Top + card.Height / 2 + app.DesiredSize.Height / 2 + panel.Inset.Bottom / 2, bounds.Top + bounds.Height / 2, 8);
        Assert.True(bounds.Top >= icon.Bottom);
        var size = style == "line" ? 4 : kind == 2 ? 5.2 : 4;
        Assert.Equal(size, bounds.Height, 8);
        Assert.Equal(style == "line" ? 4 * (kind == 2 ? 3.5 : 1.6) : size, bounds.Width, 8);
    }

    static IEnumerable<GeometryDrawing> Geometry(Drawing drawing) => drawing switch
    {
        GeometryDrawing geometry => [geometry],
        DrawingGroup group => group.Children.SelectMany(Geometry),
        _ => []
    };

    static void InSta(Action check)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { check(); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The isolated layout check timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
