namespace WinDock.Views;

/// <summary>Closed-form critically damped spring; elapsed time, not frame count, controls the response.</summary>
public readonly record struct PointerSpring(double Position, double Velocity = 0)
{
    public PointerSpring Step(double target, double seconds)
    {
        if (!double.IsFinite(target) || seconds <= 0) return this;
        const double omega = 38;
        var dt = Math.Min(seconds, 0.1);
        var displacement = Position - target;
        var coefficient = Velocity + omega * displacement;
        var decay = Math.Exp(-omega * dt);
        return new(target + (displacement + coefficient * dt) * decay,
            (Velocity - omega * coefficient * dt) * decay);
    }
    public bool Settled(double target) => Math.Abs(Position - target) < 0.05 && Math.Abs(Velocity) < 0.5;
}
