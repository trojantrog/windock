namespace WinDock.Views;

public static class LensGeometry
{
    public static LensLayout Layout(IReadOnlyList<double> widths, IReadOnlyList<bool> lens, double gap,
        double pointer, double amplitude, double magnification, double radius)
    {
        var n = widths.Count;
        var baseLeft = new double[n];
        var scale = new double[n];
        var total = 0.0;
        for (var i = 0; i < n; i++)
        {
            baseLeft[i] = total;
            scale[i] = 1;
            total += widths[i] + (i + 1 < n ? gap : 0);
        }
        var left = baseLeft.ToArray();
        if (n == 0 || amplitude <= 0 || magnification <= 1 || radius <= 0 || gap <= 0 || !double.IsFinite(pointer))
            return new LensLayout(left, scale, total);

        var grow = (magnification - 1) * Math.Clamp(amplitude, 0, 1);
        var growth = new double[n];
        for (var i = 0; i < n; i++)
            if (lens[i]) growth[i] = grow * LensMath.Influence(Math.Abs(baseLeft[i] + widths[i] / 2 - pointer), radius);

        for (var start = 0; start < n; start++)
        {
            if (!lens[start]) continue;
            var end = start;
            while (end + 1 < n && lens[end + 1]) end++;
            ArrangeGroup(start, end);
            start = end;
        }
        return new LensLayout(left, scale, total);

        void ArrangeGroup(int start, int end)
        {
            var requested = 0.0;
            for (var i = start; i <= end; i++) requested += widths[i] * growth[i];
            if (requested <= 0) return;

            var firstGap = Math.Max(0, start - 1);
            var lastGap = Math.Min(n - 2, end);
            var capacities = new double[Math.Max(0, lastGap - firstGap + 1)];
            var capacity = 0.0;
            for (var i = firstGap; i <= lastGap; i++)
            {
                var center = baseLeft[i] + widths[i] + gap / 2;
                var available = gap * LensMath.Influence(Math.Abs(center - pointer), 2 * radius + gap);
                capacities[i - firstGap] = available;
                capacity += available;
            }
            if (capacity <= 0) return;

            var allocated = LimitGrowth(requested, capacity);
            var fraction = allocated / requested;
            var compression = allocated / capacity;
            var position = baseLeft[start] - (start > 0 ? capacities[0] * compression : 0);
            for (var i = start; i <= end; i++)
            {
                left[i] = position;
                scale[i] = 1 + growth[i] * fraction;
                if (i < end) position += widths[i] * scale[i] + gap - capacities[i - firstGap] * compression;
            }
        }
    }

    static double LimitGrowth(double requested, double capacity)
    {
        var transition = capacity / 4;
        var full = capacity - transition;
        return requested <= full ? requested : capacity - transition * Math.Exp(-(requested - full) / transition);
    }
}
