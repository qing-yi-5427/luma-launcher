namespace LumaLauncher.Services;

/// <summary>A critically damped scalar spring. Retargeting preserves presentation and velocity.</summary>
internal sealed class CriticalSpring(double responseSeconds)
{
    private readonly double _frequency = 2 * Math.PI / responseSeconds;
    public double Value { get; private set; }
    public double Velocity { get; private set; }
    public double Target { get; private set; }

    public void Reset(double value)
    {
        Value = Target = value;
        Velocity = 0;
    }

    public void Retarget(double target) => Target = target;

    public bool Step(double seconds)
    {
        var elapsed = Math.Clamp(seconds, 0, 0.05);
        var displacement = Value - Target;
        var coefficient = Velocity + _frequency * displacement;
        var decay = Math.Exp(-_frequency * elapsed);
        Value = Target + (displacement + coefficient * elapsed) * decay;
        Velocity = (Velocity - _frequency * coefficient * elapsed) * decay;
        if (Math.Abs(Value - Target) <= 0.35 && Math.Abs(Velocity) <= 2)
        {
            Value = Target;
            Velocity = 0;
            return false;
        }
        return true;
    }
}
