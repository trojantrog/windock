namespace WinDock.Views;

public readonly record struct LensLayout(double[] Left, double[] Scale, double Total);

public static class LensMath
{
    public static double Influence(double distance, double radius) =>
        radius <= 0 || distance >= radius ? 0 : (1 + Math.Cos(Math.PI * distance / radius)) / 2;

    public static LensLayout Layout(IReadOnlyList<double> widths, IReadOnlyList<bool> lens, double gap,
        double pointer, double amplitude, double magnification, double radius)
    {
        var n = widths.Count;
        var baseLeft = new double[n];
        var scale = new double[n];
        var x = 0.0;
        for (var i = 0; i < n; i++)
        {
            baseLeft[i] = x;
            x += widths[i] + (i < n - 1 ? gap : 0);
            scale[i] = 1;
        }
        var baseTotal = x;
        if (n == 0 || amplitude <= 0 || double.IsNaN(pointer)) return new LensLayout(baseLeft, scale, baseTotal);

        var grow = (magnification - 1) * amplitude;
        for (var i = 0; i < n; i++)
            if (lens[i]) scale[i] = 1 + grow * Influence(Math.Abs(baseLeft[i] + widths[i] / 2 - pointer), radius);

        var left = new double[n];
        x = 0;
        for (var i = 0; i < n; i++)
        {
            left[i] = x;
            x += widths[i] * scale[i] + (i < n - 1 ? gap : 0);
        }
        var total = x;

        var offset = pointer - Magnified(pointer, widths, scale, baseLeft, left, baseTotal, total);
        for (var i = 0; i < n; i++) left[i] += offset;
        return new LensLayout(left, scale, total);
    }

    static double Magnified(double pointer, IReadOnlyList<double> widths, double[] scale, double[] baseLeft, double[] left,
        double baseTotal, double total)
    {
        var edge = Math.Min(4, widths[0] / 4);
        if (pointer <= 0) return pointer + GapCurve(pointer + edge, edge, 0, scale[0] - 1);
        edge = Math.Min(4, widths[^1] / 4);
        if (pointer >= baseTotal) return total + (pointer - baseTotal)
            + GapCurve(pointer - baseTotal, edge, scale[^1] - 1, 0);

        for (var k = 0; k + 1 < widths.Count; k++)
        {
            var boundary = baseLeft[k + 1];
            if (boundary != baseLeft[k] + widths[k]) continue;
            edge = Math.Min(4, Math.Min(widths[k], widths[k + 1]) / 4);
            if (edge <= 0 || pointer < boundary - edge || pointer > boundary + edge) continue;
            return Hermite((pointer - boundary + edge) / (2 * edge), 2 * edge,
                left[k] + (widths[k] - edge) * scale[k], left[k + 1] + edge * scale[k + 1], scale[k], scale[k + 1]);
        }

        var i = baseLeft.Length - 1;
        while (i > 0 && baseLeft[i] > pointer) i--;
        var inside = pointer - baseLeft[i];
        return inside <= widths[i]
            ? left[i] + inside * scale[i]
            : left[i] + widths[i] * scale[i] + (inside - widths[i])
                + GapCurve(inside - widths[i], baseLeft[i + 1] - baseLeft[i] - widths[i], scale[i] - 1, scale[i + 1] - 1);
    }

    static double GapCurve(double inside, double width, double incoming, double outgoing)
    {
        if (width <= 0 || inside <= 0 || inside >= width) return 0;
        var t = inside / width;
        return width * t * (t - 1) * (incoming * (t - 1) + outgoing * t);
    }

    static double Hermite(double t, double width, double start, double end, double incoming, double outgoing) =>
        (2 * t * t * t - 3 * t * t + 1) * start + (t * t * t - 2 * t * t + t) * width * incoming
        + (-2 * t * t * t + 3 * t * t) * end + (t * t * t - t * t) * width * outgoing;

    public static double Allowance(double magnification, double radius, double tile) =>
        Math.Max(0, magnification - 1) * (radius + tile);
}
