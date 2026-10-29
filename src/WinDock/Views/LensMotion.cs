namespace WinDock.Views;

/// <summary>Pointer animation state; the caller supplies monotonic input and render times.</summary>
public sealed class LensMotion
{
    PointerSpring pointer = new(double.NaN);
    double target = double.NaN;
    TimeSpan lastStep;
    TimeSpan? lastFrame;

    public double Position => pointer.Position;
    public bool IsTracking { get; private set; }

    public void Track(double value, TimeSpan now, bool animate)
    {
        target = value;
        if (!animate || !double.IsFinite(value) || !double.IsFinite(Position))
        { Reset(value); return; }
        if (IsTracking) return;
        if (pointer.Settled(value)) { Reset(value); return; }
        IsTracking = true;
        // Restart from the input time, so a new movement has no zero-time frame
        // and never consumes the time spent idle.
        lastStep = now;
    }

    public bool Advance(TimeSpan now, TimeSpan frame)
    {
        if (!IsTracking || now <= lastStep || lastFrame is { } previous && frame <= previous) return false;
        var seconds = (now - lastStep).TotalSeconds;
        lastStep = now;
        lastFrame = frame;
        var position = pointer.Position;
        pointer = pointer.Step(target, seconds);
        if (pointer.Settled(target)) Reset(target);
        return pointer.Position != position;
    }

    public void Reset(double value)
    {
        target = value;
        pointer = new(value);
        IsTracking = false;
    }
}
