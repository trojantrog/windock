using WinDock.Views;
using Xunit.Abstractions;

namespace WinDock.Tests;

public class LensMotionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(60)]
    [InlineData(144)]
    [InlineData(240)]
    public void Restart_advances_on_its_first_frame_by_the_actual_input_to_render_interval(int rate)
    {
        var motion = new LensMotion();
        motion.Track(0, TimeSpan.Zero, false);
        var input = TimeSpan.FromSeconds(10);
        var elapsed = TimeSpan.FromSeconds(1.0 / rate);
        motion.Track(100, input, true);
        motion.Advance(input + elapsed, TimeSpan.FromSeconds(20));
        var expected = new PointerSpring(0).Step(100, elapsed.TotalSeconds);
        Assert.Equal(expected.Position, motion.Position, 8);
        Assert.True(motion.Position > 0);

        for (var i = 1; motion.IsTracking && i <= rate; i++)
            motion.Advance(input + elapsed + TimeSpan.FromSeconds((double)i / rate), TimeSpan.FromSeconds(20 + (double)i / rate));
        Assert.False(motion.IsTracking);
        motion.Track(200, TimeSpan.FromSeconds(100), true);
        motion.Advance(TimeSpan.FromSeconds(100) + elapsed, TimeSpan.FromSeconds(200));
        Assert.Equal(100 + expected.Position, motion.Position, 8);
    }

    [Fact]
    public void The_same_render_frame_does_not_advance_or_request_another_layout()
    {
        var motion = new LensMotion();
        motion.Track(0, TimeSpan.Zero, false);
        motion.Track(100, TimeSpan.Zero, true);
        var frame = TimeSpan.FromSeconds(1);
        motion.Advance(TimeSpan.FromSeconds(0.01), frame);
        var position = motion.Position;
        Assert.False(motion.Advance(TimeSpan.FromSeconds(0.012), frame));
        Assert.Equal(position, motion.Position);
        motion.Track(120, TimeSpan.FromSeconds(0.013), true);
        Assert.False(motion.Advance(TimeSpan.FromSeconds(0.014), frame));
        Assert.Equal(position, motion.Position);
        Assert.True(motion.Advance(TimeSpan.FromSeconds(0.02), frame + TimeSpan.FromSeconds(0.01)));
        Assert.True(motion.Position > position);
    }

    [Fact]
    public void Irregular_frames_and_repeated_targets_preserve_the_same_elapsed_response()
    {
        var motion = new LensMotion();
        motion.Track(0, TimeSpan.Zero, false);
        motion.Track(100, TimeSpan.Zero, true);
        var expected = new PointerSpring(0);
        var elapsed = TimeSpan.Zero;
        foreach (var ticks in new[] { 5_000, 71_000, 120_000, 20_000, 330_000, 1_000, 80_000 })
        {
            var step = TimeSpan.FromTicks(ticks);
            motion.Track(100, elapsed + step / 2, true);
            elapsed += step;
            motion.Advance(elapsed, elapsed);
            expected = expected.Step(100, step.TotalSeconds);
            Assert.Equal(expected.Position, motion.Position, 8);
        }
    }

    [Fact]
    public void A_stationary_target_settles_and_requests_no_further_layouts()
    {
        var motion = new LensMotion();
        motion.Track(0, TimeSpan.Zero, false);
        motion.Track(100, TimeSpan.Zero, true);
        var now = TimeSpan.Zero;
        for (var i = 0; i < 240 && motion.IsTracking; i++)
        {
            now += TimeSpan.FromSeconds(1.0 / 144);
            motion.Advance(now, now);
        }
        Assert.False(motion.IsTracking);
        Assert.Equal(100, motion.Position);
        for (var i = 0; i < 240; i++)
        {
            now += TimeSpan.FromSeconds(1.0 / 144);
            motion.Track(100, now, true);
            Assert.False(motion.IsTracking);
            Assert.False(motion.Advance(now, now));
            Assert.Equal(100, motion.Position);
        }
    }

    [Fact]
    public void Disabled_animation_and_an_uninitialised_pointer_snap_without_tracking()
    {
        var motion = new LensMotion();
        motion.Track(40, TimeSpan.Zero, true);
        Assert.Equal(40, motion.Position);
        Assert.False(motion.IsTracking);
        motion.Track(120, TimeSpan.FromSeconds(1), false);
        Assert.Equal(120, motion.Position);
        Assert.False(motion.IsTracking);
        motion.Track(double.NaN, TimeSpan.FromSeconds(2), true);
        Assert.True(double.IsNaN(motion.Position));
        Assert.False(motion.IsTracking);
        motion.Track(30, TimeSpan.FromSeconds(3), true);
        Assert.Equal(30, motion.Position);
        Assert.False(motion.IsTracking);
    }

    [Fact]
    public void Nonadvancing_clocks_leave_the_motion_unchanged()
    {
        var motion = new LensMotion();
        motion.Track(0, TimeSpan.Zero, false);
        motion.Track(100, TimeSpan.FromSeconds(1), true);
        Assert.False(motion.Advance(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));
        Assert.False(motion.Advance(TimeSpan.Zero, TimeSpan.Zero));
        Assert.Equal(0, motion.Position);
        Assert.True(motion.Advance(TimeSpan.FromSeconds(1.01), TimeSpan.FromSeconds(1.01)));
        var position = motion.Position;
        Assert.False(motion.Advance(TimeSpan.FromSeconds(1.02), TimeSpan.FromSeconds(1)));
        Assert.Equal(position, motion.Position);
    }

    [Fact]
    public void Crossing_an_icon_edge_does_not_change_every_icons_velocity_abruptly()
    {
        double[] widths = [48, 48];
        bool[] lens = [true, true];
        const double edge = 48, step = 0.001;
        var before = LensMath.Layout(widths, lens, 8, edge - step, 1, 1.8, 140);
        var at = LensMath.Layout(widths, lens, 8, edge, 1, 1.8, 140);
        var after = LensMath.Layout(widths, lens, 8, edge + step, 1, 1.8, 140);

        for (var i = 0; i < widths.Length; i++)
        {
            double Center(LensLayout frame) => frame.Left[i] + widths[i] * frame.Scale[i] / 2;
            var incoming = (Center(at) - Center(before)) / step;
            var outgoing = (Center(after) - Center(at)) / step;
            output.WriteLine($"Icon {i} velocity at {edge}: {incoming:F6} -> {outgoing:F6}.");
            Assert.True(Math.Abs(outgoing - incoming) < 0.001,
                $"Icon {i}: incoming velocity {incoming:F6}, outgoing {outgoing:F6} at pointer {edge}.");
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.001)]
    [InlineData(0.1)]
    [InlineData(8)]
    [InlineData(11)]
    public void Every_edge_has_continuous_velocity_with_mixed_widths_and_widgets(double gap)
    {
        double[] widths = [30, 11, 96, 48, 30];
        bool[] lens = [true, false, false, true, true];
        CheckEdges(widths, lens, gap, 2.4, 140);
    }

    [Fact]
    public void User_sizing_keeps_velocity_continuous_across_all_eighteen_pins()
    {
        var widths = Enumerable.Repeat(30.0, 18).ToArray();
        var lens = Enumerable.Repeat(true, 18).ToArray();
        CheckEdges(widths, lens, 11, 1.4, 2.7 * (30 + 11));
    }

    [Fact]
    public void Whole_bar_and_status_block_do_not_change_velocity_abruptly_at_an_icon_edge()
    {
        double[] widths = [48, 48, 180];
        bool[] lens = [true, true, false];
        CheckBarEdges(widths, lens, 8, 1.8, 140, [48], output);
    }

    [Fact]
    public void Whole_bar_and_status_block_keep_continuous_velocity_with_the_users_eighteen_pins()
    {
        var widths = Enumerable.Repeat(30.0, 18).Concat([11, 180]).ToArray();
        var lens = Enumerable.Repeat(true, 18).Concat([false, false]).ToArray();
        var edges = new List<double>();
        var start = 0.0;
        foreach (var width in widths)
        {
            edges.Add(start);
            edges.Add(start + width);
            start += width + 11;
        }
        CheckBarEdges(widths, lens, 11, 1.4, 2.7 * (30 + 11), edges, output);
    }

    static double[] BarAndStatus(LensLayout frame, double[] widths)
    {
        const double origin = 200, inset = 3 + 4;
        var left = origin + frame.Left[0] - inset;
        var right = origin + frame.Left[^1] + widths[^1] * frame.Scale[^1] + inset;
        return [left, right, (left + right) / 2, origin + frame.Left[^1]];
    }

    static void CheckBarEdges(double[] widths, bool[] lens, double gap, double magnification, double radius,
        IEnumerable<double> edges, ITestOutputHelper output)
    {
        const double step = 0.0001;
        string[] names = ["Bar.Left", "Bar.Right", "Bar.Center", "Status.Left"];
        var maximum = 0.0;
        foreach (var edge in edges)
        {
            double[] Frame(double pointer) => BarAndStatus(LensMath.Layout(widths, lens, gap, pointer, 1, magnification, radius), widths);
            var beforeTwice = Frame(edge - 2 * step);
            var before = Frame(edge - step);
            var at = Frame(edge);
            var after = Frame(edge + step);
            var afterTwice = Frame(edge + 2 * step);
            for (var i = 0; i < at.Length; i++)
            {
                var incoming = (3 * at[i] - 4 * before[i] + beforeTwice[i]) / (2 * step);
                var outgoing = (-3 * at[i] + 4 * after[i] - afterTwice[i]) / (2 * step);
                var change = Math.Abs(outgoing - incoming);
                maximum = Math.Max(maximum, change);
                Assert.True(change < 0.001, $"{names[i]} at pointer {edge}: velocity {incoming:F6} -> {outgoing:F6}.");
            }
        }
        output.WriteLine($"Largest whole-bar/status velocity discontinuity: {maximum:F8} DIP/DIP.");
    }

    [Fact]
    public void Whole_bar_and_status_block_remain_stationary_after_the_pointer_settles()
    {
        var widths = Enumerable.Repeat(30.0, 18).Concat([11, 180]).ToArray();
        var lens = Enumerable.Repeat(true, 18).Concat([false, false]).ToArray();
        var motion = new LensMotion();
        motion.Track(0, TimeSpan.Zero, false);
        motion.Track(30, TimeSpan.Zero, true);
        var now = TimeSpan.Zero;
        for (var i = 0; i < 240 && motion.IsTracking; i++)
        {
            now += TimeSpan.FromSeconds(1.0 / 144);
            motion.Advance(now, now);
        }
        Assert.False(motion.IsTracking);
        var settled = BarAndStatus(LensMath.Layout(widths, lens, 11, motion.Position, 1, 1.4, 110.7), widths);
        for (var i = 0; i < 240; i++)
        {
            now += TimeSpan.FromSeconds(1.0 / 144);
            motion.Track(30, now, true);
            Assert.False(motion.Advance(now, now));
            Assert.Equal(settled, BarAndStatus(LensMath.Layout(widths, lens, 11, motion.Position, 1, 1.4, 110.7), widths));
        }
        output.WriteLine("Bar.Left, Bar.Right, Bar.Center and Status.Left: 0 DIP drift in 240 stationary frames.");
    }

    static void CheckEdges(double[] widths, bool[] lens, double gap, double magnification, double radius)
    {
        var step = gap > 0 ? Math.Min(0.0001, gap / 1000) : 0.0001;
        var boundaries = new List<double>();
        var at = 0.0;
        for (var i = 0; i < widths.Length; i++)
        {
            boundaries.Add(at);
            boundaries.Add(at + widths[i]);
            if (gap == 0 && i + 1 < widths.Length)
            {
                var band = Math.Min(4, Math.Min(widths[i], widths[i + 1]) / 4);
                boundaries.Add(at + widths[i] - band);
                boundaries.Add(at + widths[i] + band);
            }
            at += widths[i] + (i + 1 < widths.Length ? gap : 0);
        }
        boundaries.Add(-Math.Min(4, widths[0] / 4));
        boundaries.Add(at + Math.Min(4, widths[^1] / 4));
        foreach (var boundary in boundaries)
        {
            var before = LensMath.Layout(widths, lens, gap, boundary - step, 1, magnification, radius);
            var beforeTwice = LensMath.Layout(widths, lens, gap, boundary - 2 * step, 1, magnification, radius);
            var frame = LensMath.Layout(widths, lens, gap, boundary, 1, magnification, radius);
            var after = LensMath.Layout(widths, lens, gap, boundary + step, 1, magnification, radius);
            var afterTwice = LensMath.Layout(widths, lens, gap, boundary + 2 * step, 1, magnification, radius);
            for (var i = 0; i < widths.Length; i++)
            {
                double Center(LensLayout layout) => layout.Left[i] + widths[i] * layout.Scale[i] / 2;
                var incoming = (3 * Center(frame) - 4 * Center(before) + Center(beforeTwice)) / (2 * step);
                var outgoing = (-3 * Center(frame) + 4 * Center(after) - Center(afterTwice)) / (2 * step);
                Assert.True(Math.Abs(outgoing - incoming) < 0.001,
                    $"gap={gap}, edge={boundary}, icon={i}: {incoming:F6} -> {outgoing:F6}");
            }
        }
    }

    [Theory]
    [InlineData(0.001)]
    [InlineData(8)]
    [InlineData(11)]
    public void Nonzero_gaps_preserve_exact_anchor_inside_every_tile(double gap)
    {
        double[] widths = [30, 11, 96, 48, 30];
        bool[] lens = [true, false, false, true, true];
        var start = 0.0;
        for (var i = 0; i < widths.Length; i++)
        {
            for (var sample = 0; sample <= 100; sample++)
            {
                var inside = widths[i] * sample / 100;
                var pointer = start + inside;
                var frame = LensMath.Layout(widths, lens, gap, pointer, 1, 2.4, 140);
                Assert.Equal(pointer, frame.Left[i] + inside * frame.Scale[i], 8);
                for (var k = 1; k < widths.Length; k++)
                    Assert.Equal(gap, frame.Left[k] - frame.Left[k - 1] - widths[k - 1] * frame.Scale[k - 1], 8);
            }
            start += widths[i] + gap;
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.001)]
    [InlineData(0.1)]
    [InlineData(11)]
    public void Anchor_mapping_remains_monotone_at_maximum_magnification(double gap)
    {
        double[] widths = [30, 96];
        foreach (var lens in new[] { new[] { false, false }, [true, false], [false, true], [true, true] })
        {
            double Map(double pointer)
            {
                var frame = LensMath.Layout(widths, lens, gap, pointer, 1, 2.4, double.PositiveInfinity);
                return pointer - frame.Left[0];
            }
            void CheckInterval(double start, double end)
            {
                const int samples = 1000;
                var step = (end - start) / samples;
                var previous = Map(start);
                for (var i = 1; i <= samples; i++)
                {
                    var current = Map(start + i * step);
                    var derivative = (current - previous) / step;
                    Assert.InRange(derivative, 0.29999, 3.8);
                    previous = current;
                }
            }
            CheckInterval(-4, 0);
            CheckInterval(0, 30);
            if (gap > 0) CheckInterval(30, 30 + gap);
            else CheckInterval(26, 34);
            CheckInterval(30 + gap, 126 + gap);
            CheckInterval(126 + gap, 130 + gap);
        }
    }

    [Fact]
    public void Touching_different_tiles_limit_the_local_anchor_change_to_one_point_four_DIP()
    {
        double[] widths = [96, 96];
        bool[] lens = [false, true];
        var maximum = 0.0;
        for (var i = 0; i <= 2000; i++)
        {
            var pointer = 92 + i * 8.0 / 2000;
            var frame = LensMath.Layout(widths, lens, 0, pointer, 1, 2.4, double.PositiveInfinity);
            var mapped = pointer - frame.Left[0];
            var linear = pointer <= 96 ? pointer : 96 + (pointer - 96) * 2.4;
            maximum = Math.Max(maximum, Math.Abs(mapped - linear));
            Assert.InRange(Math.Abs(mapped - linear), 0, 1.4 + 1e-10);
        }
        output.WriteLine($"Maximum zero-gap anchor change: {maximum:F6} DIP (magnification 2.4, 4 DIP band each side).");
        Assert.Equal(1.4, maximum, 8);
    }

    [Fact]
    public void Touching_tiles_keep_their_centers_and_points_outside_the_transition_exact()
    {
        double[] widths = [30, 11, 96, 48];
        bool[] lens = [true, false, false, true];
        var start = 0.0;
        for (var i = 0; i < widths.Length; i++)
        {
            var band = Math.Min(4, widths[i] / 4);
            foreach (var inside in new[] { band, widths[i] / 2, widths[i] - band })
            {
                var pointer = start + inside;
                var frame = LensMath.Layout(widths, lens, 0, pointer, 1, 2.4, 140);
                Assert.Equal(pointer, frame.Left[i] + inside * frame.Scale[i], 8);
            }
            start += widths[i];
        }
    }

    [Fact]
    public void A_stationary_pointer_keeps_the_same_icon_geometry()
    {
        double[] widths = [30, 30, 11, 96, 30];
        bool[] lens = [true, true, false, false, true];
        var frame = LensMath.Layout(widths, lens, 11, 41, 1, 1.4, 110.7);
        for (var i = 0; i < 240; i++)
        {
            var next = LensMath.Layout(widths, lens, 11, 41, 1, 1.4, 110.7);
            Assert.Equal(frame.Left, next.Left);
            Assert.Equal(frame.Scale, next.Scale);
        }
    }
}
