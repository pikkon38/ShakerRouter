using System;

namespace ShakerRouter.Audio.Dsp;

/// <summary>
/// RBJ biquad, transposed direct form II, double precision.
/// Double precision matters here. At 48 kHz an 80 Hz cutoff puts the poles very close
/// to the unit circle, and float coefficients/state drift into noise and DC offset.
/// Struct so an array of filters lives in one contiguous block with no allocations
/// on the audio thread. Always call through the array element (filters[i].Process),
/// never through a copy.
/// </summary>
public struct Biquad
{
    private double _b0, _b1, _b2, _a1, _a2;
    private double _z1, _z2;

    public void SetLowPass(double sampleRate, double cutoffHz, double q)
    {
        cutoffHz = Math.Clamp(cutoffHz, 10.0, sampleRate * 0.45);
        double w0 = 2.0 * Math.PI * cutoffHz / sampleRate;
        double cosW0 = Math.Cos(w0);
        double alpha = Math.Sin(w0) / (2.0 * q);
        double a0 = 1.0 + alpha;

        _b0 = (1.0 - cosW0) * 0.5 / a0;
        _b1 = (1.0 - cosW0) / a0;
        _b2 = _b0;
        _a1 = -2.0 * cosW0 / a0;
        _a2 = (1.0 - alpha) / a0;
        // State (_z1, _z2) is intentionally kept so cutoff changes don't click.
    }

    public void SetHighPass(double sampleRate, double cutoffHz, double q)
    {
        cutoffHz = Math.Clamp(cutoffHz, 5.0, sampleRate * 0.45);
        double w0 = 2.0 * Math.PI * cutoffHz / sampleRate;
        double cosW0 = Math.Cos(w0);
        double alpha = Math.Sin(w0) / (2.0 * q);
        double a0 = 1.0 + alpha;

        _b0 = (1.0 + cosW0) * 0.5 / a0;
        _b1 = -(1.0 + cosW0) / a0;
        _b2 = _b0;
        _a1 = -2.0 * cosW0 / a0;
        _a2 = (1.0 - alpha) / a0;
    }

    public double Process(double x)
    {
        double y = _b0 * x + _z1;
        _z1 = _b1 * x - _a1 * y + _z2;
        _z2 = _b2 * x - _a2 * y;
        return y;
    }

    public void Reset()
    {
        _z1 = 0;
        _z2 = 0;
    }
}

/// <summary>
/// 4th order Linkwitz-Riley low pass: two cascaded Butterworth biquads (Q = 0.7071).
/// 24 dB/octave. With an 80 Hz cutoff, dialogue around 300 Hz is down roughly 45 dB,
/// which is what keeps voices out of the seat. A single 12 dB/oct biquad leaks too much.
/// </summary>
public struct LowPass24
{
    private const double ButterworthQ = 0.70710678118654752;
    private Biquad _stage1;
    private Biquad _stage2;

    public void Configure(double sampleRate, double cutoffHz)
    {
        _stage1.SetLowPass(sampleRate, cutoffHz, ButterworthQ);
        _stage2.SetLowPass(sampleRate, cutoffHz, ButterworthQ);
    }

    public double Process(double x) => _stage2.Process(_stage1.Process(x));

    public void Reset()
    {
        _stage1.Reset();
        _stage2.Reset();
    }
}

/// <summary>
/// 4th order Linkwitz-Riley high pass (subsonic filter), 24 dB/octave.
/// Removes content below what a small transducer can reproduce, so its power goes
/// into frequencies you can feel.
/// </summary>
public struct HighPass24
{
    private const double ButterworthQ = 0.70710678118654752;
    private Biquad _stage1;
    private Biquad _stage2;

    public void Configure(double sampleRate, double cutoffHz)
    {
        _stage1.SetHighPass(sampleRate, cutoffHz, ButterworthQ);
        _stage2.SetHighPass(sampleRate, cutoffHz, ButterworthQ);
    }

    public double Process(double x) => _stage2.Process(_stage1.Process(x));
}
