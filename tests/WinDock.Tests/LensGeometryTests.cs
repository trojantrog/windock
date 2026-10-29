using WinDock.Views;
using Xunit.Abstractions;

namespace WinDock.Tests;

public class LensGeometryTests(ITestOutputHelper output)
{
    static readonly double[] UserWidths = Enumerable.Repeat(30.0, 18).Concat([11, 180]).ToArray();
    static readonly bool[] UserLens = Enumerable.Repeat(true, 18).Concat([false, false]).ToArray();

    [Fact]
    public void Horizontal_sweep_keeps_the_plate_and_status_at_their_resting_bounds()
    {
        var resting = LensGeometry.Layout(UserWidths, UserLens, 11, 0, 0, 1.4, 110.7);
        var frames = Enumerable.Range(0, 728)
            .Select(pointer => LensGeometry.Layout(UserWidths, UserLens, 11, pointer, 1, 1.4, 110.7)).ToArray();
        var widthTravel = frames.Max(f => f.Total) - frames.Min(f => f.Total);
        var statusTravel = frames.Max(f => f.Left[^1]) - frames.Min(f => f.Left[^1]);
        output.WriteLine($"Horizontal sweep: plate width travel {widthTravel:F8} DIP; status travel {statusTravel:F8} DIP.");
        Assert.True(widthTravel < 1e-8, $"Plate width changes by {widthTravel:F8} DIP during a horizontal sweep.");
        Assert.True(statusTravel < 1e-8, $"Status moves by {statusTravel:F8} DIP during a horizontal sweep.");
        for (var pointer = 0; pointer < frames.Length; pointer++)
        {
            AssertFixedBounds(resting, frames[pointer], UserWidths, UserLens);
            for (var icon = 0; icon < 18; icon++)
                Assert.Equal(1 + 0.4 * LensMath.Influence(Math.Abs(icon * 41 + 15 - pointer), 110.7), frames[pointer].Scale[icon], 8);
        }
    }

    [Fact]
    public void Enter_sweep_status_hover_and_exit_keep_the_same_plate_and_nonlens_positions()
    {
        var resting = LensGeometry.Layout(UserWidths, UserLens, 11, 15, 0, 1.4, 110.7);
        for (var frame = 0; frame <= 60; frame++)
        {
            AssertFixedBounds(resting, LensGeometry.Layout(UserWidths, UserLens, 11, 15, frame / 60.0, 1.4, 110.7), UserWidths, UserLens);
            AssertFixedBounds(resting, LensGeometry.Layout(UserWidths, UserLens, 11, 850, 1 - frame / 60.0, 1.4, 110.7), UserWidths, UserLens);
        }
        for (var pointer = -120; pointer <= 1060; pointer++)
            AssertFixedBounds(resting, LensGeometry.Layout(UserWidths, UserLens, 11, pointer, 1, 1.4, 110.7), UserWidths, UserLens);
    }

    [Fact]
    public void The_users_current_gap_allows_full_requested_magnification_on_every_pin()
    {
        for (var icon = 0; icon < 18; icon++)
        {
            var pointer = icon * 41 + 15;
            var frame = LensGeometry.Layout(UserWidths, UserLens, 11, pointer, 1, 1.4, 110.7);
            Assert.True(Math.Abs(frame.Scale[icon] - 1.4) < 1e-8, $"Pin {icon} reaches {frame.Scale[icon]:F10}, expected 1.4.");
            for (var i = 0; i + 1 < UserWidths.Length; i++)
                Assert.InRange(frame.Left[i + 1] - frame.Left[i] - UserWidths[i] * frame.Scale[i], -1e-8, 11 + 1e-8);
        }
    }

    [Fact]
    public void Symmetric_neighborhood_keeps_the_icon_centered_under_the_pointer()
    {
        const int icon = 9;
        const double pointer = icon * 41 + 15;
        var frame = LensGeometry.Layout(UserWidths, UserLens, 11, pointer, 1, 1.4, 110.7);
        Assert.Equal(pointer, frame.Left[icon] + UserWidths[icon] * frame.Scale[icon] / 2, 8);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(144)]
    [InlineData(240)]
    public void Smoothed_horizontal_sweeps_keep_the_plate_and_status_fixed_at_every_frame(int rate)
    {
        var resting = LensGeometry.Layout(UserWidths, UserLens, 11, 15, 0, 1.4, 110.7);
        var motion = new LensMotion();
        motion.Track(15, TimeSpan.Zero, false);
        for (var frame = 1; frame <= 3 * rate; frame++)
        {
            var seconds = (double)frame / rate;
            var pointer = 15 + 712 * (1 - Math.Cos(2 * Math.PI * seconds)) / 2;
            var now = TimeSpan.FromSeconds(seconds);
            motion.Track(pointer, now, true);
            motion.Advance(now, now);
            var amplitude = Math.Clamp(Math.Min(seconds / 0.14, (3 - seconds) / 0.22), 0, 1);
            AssertFixedBounds(resting, LensGeometry.Layout(UserWidths, UserLens, 11, motion.Position, amplitude, 1.4, 110.7), UserWidths, UserLens);
        }
    }

    [Theory]
    [InlineData(0.001)]
    [InlineData(11)]
    [InlineData(24)]
    public void Mixed_icon_and_widget_edges_keep_continuous_local_velocity(double gap)
    {
        double[] widths = [30, 30, 11, 96, 48, 30, 180];
        bool[] lens = [true, true, false, false, true, true, false];
        var resting = LensGeometry.Layout(widths, lens, gap, 0, 0, 2.4, 110.7);
        const double step = 0.0001;
        var maximum = 0.0;
        foreach (var edge in resting.Left.SelectMany((left, i) => new[] { left, left + widths[i] }))
        {
            LensLayout Frame(double pointer) => LensGeometry.Layout(widths, lens, gap, pointer, 1, 2.4, 110.7);
            var beforeTwice = Frame(edge - 2 * step);
            var before = Frame(edge - step);
            var at = Frame(edge);
            var after = Frame(edge + step);
            var afterTwice = Frame(edge + 2 * step);
            for (var i = 0; i < widths.Length; i++)
            {
                double Center(LensLayout geometry) => geometry.Left[i] + widths[i] * geometry.Scale[i] / 2;
                var incoming = (3 * Center(at) - 4 * Center(before) + Center(beforeTwice)) / (2 * step);
                var outgoing = (-3 * Center(at) + 4 * Center(after) - Center(afterTwice)) / (2 * step);
                maximum = Math.Max(maximum, Math.Abs(outgoing - incoming));
                Assert.True(Math.Abs(outgoing - incoming) < 0.001, $"Icon {i}, pointer {edge}: {incoming:F8} -> {outgoing:F8} DIP/DIP.");
            }
        }
        output.WriteLine($"Largest icon velocity discontinuity with gap {gap}: {maximum:F8} DIP/DIP.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.001)]
    [InlineData(1)]
    [InlineData(11)]
    [InlineData(24)]
    [InlineData(72)]
    public void Tight_gaps_mixed_widgets_and_extreme_settings_keep_every_icon_visible_and_separate(double gap)
    {
        (double[] Widths, bool[] Lens)[] scenarios =
        [
            ([30], [true]),
            ([96, 96], [true, true]),
            ([30, 11, 96, 48, 30, 180, 24, 24, 24], [true, false, false, true, true, false, true, true, true]),
            ([11, 96, 11, 180], [false, true, false, false])
        ];
        foreach (var (widths, lens) in scenarios)
        {
            var resting = LensGeometry.Layout(widths, lens, gap, 0, 0, 2.4, 110.7);
            foreach (var magnification in new[] { 1.0, 1.4, 2.4 })
                foreach (var radius in new[] { 0.0, 0.1, 30, 110.7, 1300, double.PositiveInfinity })
                    foreach (var amplitude in new[] { 0.0, 0.1, 0.5, 1 })
                        for (var sample = 0; sample <= 100; sample++)
                        {
                            var pointer = -120 + (resting.Total + 240) * sample / 100;
                            var frame = LensGeometry.Layout(widths, lens, gap, pointer, amplitude, magnification, radius);
                            AssertFixedBounds(resting, frame, widths, lens);
                            for (var i = 0; i < widths.Length; i++)
                            {
                                Assert.InRange(frame.Scale[i], 1, magnification + 1e-8);
                                Assert.InRange(frame.Left[i], -1e-8, resting.Total + 1e-8);
                                Assert.InRange(frame.Left[i] + widths[i] * frame.Scale[i], -1e-8, resting.Total + 1e-8);
                                if (i + 1 < widths.Length)
                                    Assert.InRange(frame.Left[i + 1] - frame.Left[i] - widths[i] * frame.Scale[i], -1e-8, gap + 1e-8);
                            }
                        }
        }
    }

    [Fact]
    public void Icons_with_no_available_gap_keep_their_base_size()
    {
        var frame = LensGeometry.Layout([96, 96, 180], [true, true, false], 0, 96, 1, 2.4, 300);
        Assert.Equal([0, 96, 192], frame.Left);
        Assert.Equal([1, 1, 1], frame.Scale);
        Assert.Equal(372, frame.Total);
    }

    [Fact]
    public void Space_limiting_joins_full_magnification_with_continuous_velocity()
    {
        const double knee = 1.0625, step = 1e-6;
        double Scale(double magnification) => LensGeometry.Layout([48, 48], [true, true], 8, 52, 1, magnification, double.PositiveInfinity).Scale[0];
        Assert.Equal(knee - step, Scale(knee - step), 10);
        Assert.Equal(knee, Scale(knee), 10);
        var incoming = (Scale(knee) - Scale(knee - step)) / step;
        var outgoing = (Scale(knee + step) - Scale(knee)) / step;
        Assert.True(Math.Abs(outgoing - incoming) < 0.0001);
        Assert.InRange(Scale(2.4), knee, 1 + 8.0 / 96);
    }

    [Fact]
    public void Gap_changes_stay_inside_the_local_compensation_radius()
    {
        var widths = Enumerable.Repeat(30.0, 40).ToArray();
        var lens = Enumerable.Repeat(true, 40).ToArray();
        const double pointer = 20 * 41 + 15, radius = 110.7;
        var resting = LensGeometry.Layout(widths, lens, 11, pointer, 0, 1.4, radius);
        var frame = LensGeometry.Layout(widths, lens, 11, pointer, 1, 1.4, radius);
        for (var i = 0; i < widths.Length; i++)
        {
            if (Math.Abs(resting.Left[i] + widths[i] / 2 - pointer) < 2 * radius + widths[i] + 11) continue;
            Assert.Equal(resting.Left[i], frame.Left[i], 8);
            Assert.Equal(1, frame.Scale[i]);
        }
    }

    [Fact]
    public void Empty_and_uninitialised_layouts_keep_the_resting_geometry()
    {
        var empty = LensGeometry.Layout([], [], 11, 0, 1, 1.4, 110.7);
        Assert.Empty(empty.Left);
        Assert.Empty(empty.Scale);
        Assert.Equal(0, empty.Total);
        var resting = LensGeometry.Layout(UserWidths, UserLens, 11, 0, 0, 1.4, 110.7);
        var uninitialised = LensGeometry.Layout(UserWidths, UserLens, 11, double.NaN, 1, 1.4, 110.7);
        Assert.Equal(resting.Left, uninitialised.Left);
        Assert.Equal(resting.Scale, uninitialised.Scale);
        Assert.Equal(resting.Total, uninitialised.Total);
    }

    static void AssertFixedBounds(LensLayout resting, LensLayout frame, double[] widths, bool[] lens)
    {
        Assert.Equal(resting.Total, frame.Total, 8);
        Assert.Equal(resting.Left[0], frame.Left[0], 8);
        Assert.Equal(resting.Left[^1] + widths[^1], frame.Left[^1] + widths[^1] * frame.Scale[^1], 8);
        for (var i = 0; i < lens.Length; i++)
        {
            if (lens[i]) continue;
            Assert.Equal(1, frame.Scale[i]);
            Assert.Equal(resting.Left[i], frame.Left[i], 8);
        }
    }
}
