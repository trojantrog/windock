using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WinDock.Views;

public enum LensMode { None, Scale, Magnify }

public sealed class LensPanel : Panel
{
    const double ScaleCap = 0.3;

    public static readonly DependencyProperty PointerProperty = DependencyProperty.Register(nameof(Pointer), typeof(double), typeof(LensPanel),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsArrange, (d, _) => ((LensPanel)d).TrackPointer()));
    public static readonly DependencyProperty AmplitudeProperty = Value(nameof(Amplitude), 0.0, FrameworkPropertyMetadataOptions.AffectsArrange);
    public static readonly DependencyProperty MagnificationProperty = Value(nameof(Magnification), 1.75, FrameworkPropertyMetadataOptions.AffectsArrange);
    public static readonly DependencyProperty RadiusProperty = Value(nameof(Radius), 130.0, FrameworkPropertyMetadataOptions.AffectsArrange);
    public static readonly DependencyProperty GapProperty = Value(nameof(Gap), 8.0, FrameworkPropertyMetadataOptions.AffectsMeasure);
    public static readonly DependencyProperty CornerProperty = Value(nameof(Corner), 22.0, FrameworkPropertyMetadataOptions.AffectsRender);
    public static readonly DependencyProperty StrokeWidthProperty = Value(nameof(StrokeWidth), 1.0, FrameworkPropertyMetadataOptions.AffectsRender);
    public static readonly DependencyProperty SurfaceOpacityProperty = Value(nameof(SurfaceOpacity), 0.9, FrameworkPropertyMetadataOptions.AffectsRender);
    public static readonly DependencyProperty IndicatorSizeProperty = Value(nameof(IndicatorSize), 4, FrameworkPropertyMetadataOptions.AffectsRender);
    public static readonly DependencyProperty IndicatorStyleProperty = DependencyProperty.Register(nameof(IndicatorStyle), typeof(string), typeof(LensPanel),
        new FrameworkPropertyMetadata("line", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(nameof(Mode), typeof(LensMode), typeof(LensPanel),
        new FrameworkPropertyMetadata(LensMode.Magnify, FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty InsetProperty = DependencyProperty.Register(nameof(Inset), typeof(Thickness), typeof(LensPanel),
        new FrameworkPropertyMetadata(new Thickness(12, 10, 12, 12), FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty BarBrushProperty = Paint(nameof(BarBrush));
    public static readonly DependencyProperty BarStrokeProperty = Paint(nameof(BarStroke));
    public static readonly DependencyProperty GlowBrushProperty = Paint(nameof(GlowBrush));
    public static readonly DependencyProperty IndicatorBrushProperty = Paint(nameof(IndicatorBrush));
    public static readonly DependencyProperty FocusBrushProperty = Paint(nameof(FocusBrush));
    public static readonly DependencyProperty ShadowBrushProperty = Paint(nameof(ShadowBrush));

    public static readonly DependencyProperty LensProperty = DependencyProperty.RegisterAttached("Lens", typeof(bool), typeof(LensPanel),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public static readonly DependencyProperty IndicatorProperty = DependencyProperty.RegisterAttached("Indicator", typeof(int), typeof(LensPanel),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsParentArrange));

    static readonly Brush Catcher = Frozen(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)));

    readonly Dictionary<UIElement, (ScaleTransform Scale, TranslateTransform Offset)> transforms = [];
    readonly List<(Point Center, int Kind)> indicators = [];
    readonly LensMotion pointerMotion = new();
    readonly Stopwatch pointerClock = Stopwatch.StartNew();
    bool tracking;

    public LensPanel() => Unloaded += (_, _) => { pointerMotion.Reset(Pointer); StopTracking(); };

    void TrackPointer()
    {
        pointerMotion.Track(Pointer, pointerClock.Elapsed, SystemParameters.ClientAreaAnimation && Amplitude >= 0.001);
        if (!pointerMotion.IsTracking) { StopTracking(); return; }
        if (tracking) return;
        tracking = true; CompositionTarget.Rendering += StepPointer;
    }

    void StepPointer(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs frame) return;
        var changed = pointerMotion.Advance(pointerClock.Elapsed, frame.RenderingTime);
        if (!pointerMotion.IsTracking) StopTracking();
        if (changed) InvalidateArrange();
    }

    void StopTracking()
    {
        if (tracking) CompositionTarget.Rendering -= StepPointer;
        tracking = false;
    }

    public double Pointer { get => (double)GetValue(PointerProperty); set => SetValue(PointerProperty, value); }
    public double Amplitude { get => (double)GetValue(AmplitudeProperty); set => SetValue(AmplitudeProperty, value); }
    public double Magnification { get => (double)GetValue(MagnificationProperty); set => SetValue(MagnificationProperty, value); }
    public double Radius { get => (double)GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }
    public double Corner { get => (double)GetValue(CornerProperty); set => SetValue(CornerProperty, value); }
    public double StrokeWidth { get => (double)GetValue(StrokeWidthProperty); set => SetValue(StrokeWidthProperty, value); }
    public double SurfaceOpacity { get => (double)GetValue(SurfaceOpacityProperty); set => SetValue(SurfaceOpacityProperty, value); }
    public double IndicatorSize { get => (double)GetValue(IndicatorSizeProperty); set => SetValue(IndicatorSizeProperty, value); }
    public string IndicatorStyle { get => (string)GetValue(IndicatorStyleProperty); set => SetValue(IndicatorStyleProperty, value); }
    public LensMode Mode { get => (LensMode)GetValue(ModeProperty); set => SetValue(ModeProperty, value); }
    public Thickness Inset { get => (Thickness)GetValue(InsetProperty); set => SetValue(InsetProperty, value); }
    public Brush? BarBrush { get => (Brush?)GetValue(BarBrushProperty); set => SetValue(BarBrushProperty, value); }
    public Brush? BarStroke { get => (Brush?)GetValue(BarStrokeProperty); set => SetValue(BarStrokeProperty, value); }
    public Brush? GlowBrush { get => (Brush?)GetValue(GlowBrushProperty); set => SetValue(GlowBrushProperty, value); }
    public Brush? IndicatorBrush { get => (Brush?)GetValue(IndicatorBrushProperty); set => SetValue(IndicatorBrushProperty, value); }
    public Brush? FocusBrush { get => (Brush?)GetValue(FocusBrushProperty); set => SetValue(FocusBrushProperty, value); }
    public Brush? ShadowBrush { get => (Brush?)GetValue(ShadowBrushProperty); set => SetValue(ShadowBrushProperty, value); }

    public static bool GetLens(DependencyObject o) => (bool)o.GetValue(LensProperty);
    public static void SetLens(DependencyObject o, bool value) => o.SetValue(LensProperty, value);
    public static int GetIndicator(DependencyObject o) => (int)o.GetValue(IndicatorProperty);
    public static void SetIndicator(DependencyObject o, int value) => o.SetValue(IndicatorProperty, value);

    public Rect Bar { get; private set; }
    public double BaseWidth { get; private set; }
    double lensHitTop;

    public event EventHandler? Arranged;

    protected override Size MeasureOverride(Size available)
    {
        var free = new Size(double.PositiveInfinity, double.PositiveInfinity);
        double width = 0, height = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(free);
            width += child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
        }
        var inset = Inset;
        width += Gap * Math.Max(0, InternalChildren.Count - 1) + inset.Left + inset.Right;
        BaseWidth = width;
        return new Size(width, height + inset.Top + inset.Bottom);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var children = InternalChildren.Cast<UIElement>().ToList();
        var n = children.Count;
        indicators.Clear();
        if (n == 0)
        {
            Bar = Rect.Empty;
            InvalidateVisual();
            return final;
        }

        var inset = Inset;
        var widths = children.Select(c => c.DesiredSize.Width).ToArray();
        var lens = children.Select(GetLens).ToArray();
        var tileHeight = children.Max(c => c.DesiredSize.Height);
        var baseTotal = widths.Sum() + Gap * (n - 1);
        var x0 = Math.Round((final.Width - baseTotal) / 2);
        var bottom = final.Height - inset.Bottom;
        var amplitude = Mode == LensMode.None ? 0 : Amplitude;
        var pointer = pointerMotion.Position - x0;

        var layout = Mode == LensMode.Magnify
            ? LensGeometry.Layout(widths, lens, Gap, pointer, amplitude, Magnification, Radius)
            : LensMath.Layout(widths, lens, Gap, pointer, 0, Magnification, Radius);
        var hovered = -1;
        if (Mode == LensMode.Scale && amplitude > 0)
            for (var i = 0; i < n; i++)
                if (lens[i] && pointer >= layout.Left[i] && pointer < layout.Left[i] + widths[i] + Gap) hovered = i;

        var baseLeft = 0.0;
        for (var i = 0; i < n; i++)
        {
            var child = children[i];
            var w = widths[i];
            var h = child.DesiredSize.Height;
            var scale = i == hovered ? 1 + Math.Min(Magnification - 1, ScaleCap) * amplitude : layout.Scale[i];
            var slot = Mode == LensMode.Magnify ? w * layout.Scale[i] : w;
            var left = x0 + layout.Left[i];
            var childBottom = bottom - (tileHeight - h) / 2;
            child.Arrange(new Rect(x0 + baseLeft, childBottom - h, w, h));
            Transform(child, scale, layout.Left[i] - baseLeft + (slot - w) / 2);

            var kind = GetIndicator(child);
            if (kind > 0) indicators.Add((new Point(left + slot / 2, childBottom + inset.Bottom / 2), kind));
            baseLeft += w + Gap;
        }

        Bar = new Rect(x0 - inset.Left, bottom - tileHeight - inset.Top,
            baseTotal + inset.Left + inset.Right, tileHeight + inset.Top + inset.Bottom);
        var maximumScale = Mode == LensMode.Magnify ? Magnification : 1 + Math.Min(Magnification - 1, ScaleCap);
        lensHitTop = Math.Max(0, bottom - tileHeight * Math.Max(1, maximumScale) - inset.Top);
        InvalidateVisual();
        Arranged?.Invoke(this, EventArgs.Empty);
        return final;
    }

    void Transform(UIElement child, double scale, double offset)
    {
        if (!transforms.TryGetValue(child, out var t))
        {
            t = (new ScaleTransform(), new TranslateTransform());
            transforms[child] = t;
            child.RenderTransformOrigin = new Point(0.5, 1);
            var group = new TransformGroup();
            group.Children.Add(t.Scale);
            group.Children.Add(t.Offset);
            child.RenderTransform = group;
        }
        if (t.Scale.ScaleX != scale) t.Scale.ScaleX = t.Scale.ScaleY = scale;
        if (t.Offset.X != offset) t.Offset.X = offset;
    }

    protected override void OnVisualChildrenChanged(DependencyObject? added, DependencyObject? removed)
    {
        base.OnVisualChildrenChanged(added, removed);
        if (removed is UIElement gone) transforms.Remove(gone);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Bar.IsEmpty) return;
        var bar = Bar;
        var corner = Math.Min(Corner, bar.Height / 2);

        if (Amplitude > 0.01 && Mode != LensMode.None)
        {
            dc.DrawRectangle(Catcher, null, new Rect(bar.Left, lensHitTop, bar.Width, Math.Max(0, bar.Top - lensHitTop)));
        }

        dc.PushOpacity(Math.Clamp(SurfaceOpacity, 0, 1));
        if (ShadowBrush is { } shadow)
            for (var k = 1; k <= 3; k++)
            {
                var r = bar;
                r.Inflate(k * 1.5, k * 1.5);
                r.Offset(0, k);
                dc.DrawRoundedRectangle(shadow, null, r, corner + k * 1.5, corner + k * 1.5);
            }

        var pen = BarStroke is { } stroke ? new Pen(stroke, StrokeWidth) : null;
        var inner = bar;
        inner.Inflate(-StrokeWidth / 2, -StrokeWidth / 2);
        dc.DrawRoundedRectangle(BarBrush, pen, inner, corner, corner);
        if (GlowBrush is { } glow) dc.DrawRoundedRectangle(glow, null, inner, corner, corner);
        dc.Pop();

        foreach (var (center, kind) in indicators)
        {
            if (IndicatorStyle == "none") continue;
            var ink = kind == 2 ? FocusBrush ?? IndicatorBrush : IndicatorBrush;
            if (ink is null) continue;
            var size = Math.Clamp(IndicatorSize, 1, 8);
            var active = kind == 2;
            if (IndicatorStyle == "dot") dc.DrawEllipse(ink, null, center, size * (active ? 0.65 : 0.5), size * (active ? 0.65 : 0.5));
            else if (IndicatorStyle == "square")
            {
                var side = size * (active ? 1.3 : 1);
                dc.DrawRoundedRectangle(ink, null, new Rect(center.X - side / 2, center.Y - side / 2, side, side), 1, 1);
            }
            else
            {
                var width = size * (active ? 3.5 : 1.6);
                dc.DrawRoundedRectangle(ink, null, new Rect(center.X - width / 2, center.Y - size / 2, width, size), size / 2, size / 2);
            }
        }
    }

    static DependencyProperty Value(string name, double value, FrameworkPropertyMetadataOptions options) =>
        DependencyProperty.Register(name, typeof(double), typeof(LensPanel), new FrameworkPropertyMetadata(value, options));

    static DependencyProperty Paint(string name) =>
        DependencyProperty.Register(name, typeof(Brush), typeof(LensPanel), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    static Brush Frozen(Brush b)
    {
        b.Freeze();
        return b;
    }
}
