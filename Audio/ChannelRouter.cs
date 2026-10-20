using System;
using System.Threading;
using NAudio.Wave;
using ShakerRouter.Audio.Dsp;

namespace ShakerRouter.Audio;

/// <summary>
/// Immutable routing state. The UI thread builds a new snapshot on every change and
/// publishes it atomically. The audio thread reads one snapshot per buffer.
/// No locks on the audio thread, no torn reads, no allocations per buffer.
/// </summary>
public sealed class RoutingSnapshot
{
    public const int MaxInputs = 8;
    public const double DefaultCutoffHz = 80.0;
    public const double DefaultHighPassHz = 20.0;

    public required int OutputChannels { get; init; }

    /// <summary>Flat [input * OutputChannels + output]. 0 = off, 1 = on. Fractional weights are allowed.</summary>
    public required float[] Matrix { get; init; }

    /// <summary>Per output gain multiplier, 0.0 to 2.0 (200% overboost).</summary>
    public required float[] Gains { get; init; }

    /// <summary>Per game channel level, 0.0 to 1.0. Scales that channel everywhere it is routed.</summary>
    public required float[] InputGains { get; init; }

    /// <summary>Per output "Bass only" toggle.</summary>
    public required bool[] LowPassEnabled { get; init; }

    /// <summary>Per output low pass cutoff in Hz.</summary>
    public required double[] Cutoffs { get; init; }

    /// <summary>Per output high pass (subsonic) toggle and cutoff in Hz.</summary>
    public required bool[] HighPassEnabled { get; init; }
    public required double[] HighPassCutoffs { get; init; }

    /// <summary>Per output transducer preset id (see TransducerPresets). Empty = custom.</summary>
    public required string[] PresetIds { get; init; }

    public required bool[] Muted { get; init; }

    /// <summary>Output channel receiving the test tone. -1 = test tone off.</summary>
    public int TestToneChannel { get; init; } = -1;
    public double TestToneHz { get; init; } = 45.0;
    public float TestToneLevel { get; init; } = 0.5f;

    /// <summary>Pulsed tone (on/off) is far easier to locate by feel than a steady hum.</summary>
    public bool TestTonePulsed { get; init; } = true;

    /// <summary>When true, game audio is muted while a test tone plays so you only feel the tone.</summary>
    public bool MuteGameDuringTest { get; init; } = true;

    public static RoutingSnapshot CreateDefault(int outputChannels) => new()
    {
        OutputChannels = outputChannels,
        Matrix = new float[MaxInputs * outputChannels],
        Gains = Fill(outputChannels, 1.0f),
        InputGains = Fill(MaxInputs, 1.0f),
        LowPassEnabled = Fill(outputChannels, true),
        Cutoffs = Fill(outputChannels, DefaultCutoffHz),
        HighPassEnabled = new bool[outputChannels],
        HighPassCutoffs = Fill(outputChannels, DefaultHighPassHz),
        PresetIds = Fill(outputChannels, string.Empty),
        Muted = new bool[outputChannels],
    };

    public RoutingSnapshot Clone() => new()
    {
        OutputChannels = OutputChannels,
        Matrix = (float[])Matrix.Clone(),
        Gains = (float[])Gains.Clone(),
        InputGains = (float[])InputGains.Clone(),
        LowPassEnabled = (bool[])LowPassEnabled.Clone(),
        Cutoffs = (double[])Cutoffs.Clone(),
        HighPassEnabled = (bool[])HighPassEnabled.Clone(),
        HighPassCutoffs = (double[])HighPassCutoffs.Clone(),
        PresetIds = (string[])PresetIds.Clone(),
        Muted = (bool[])Muted.Clone(),
        TestToneChannel = TestToneChannel,
        TestToneHz = TestToneHz,
        TestToneLevel = TestToneLevel,
        TestTonePulsed = TestTonePulsed,
        MuteGameDuringTest = MuteGameDuringTest,
    };

    public float GetWeight(int input, int output) => Matrix[input * OutputChannels + output];

    private static T[] Fill<T>(int n, T value)
    {
        var a = new T[n];
        Array.Fill(a, value);
        return a;
    }
}

/// <summary>
/// The multiplexer. Pulls 8-channel float audio (the captured game stream, normalized to
/// FL FR C LFE RL RR SL SR), sums it through the routing matrix into M output channels,
/// then per output applies: 24 dB/oct low pass (crossfaded on toggle), smoothed gain up to
/// 2.0x, and a soft clipper. Also generates a per-channel test tone for wiring checks.
///
/// Pipeline:
///   ProcessLoopbackCapture -> BufferedWaveProvider (8ch float)
///     -> ToSampleProvider() -> ChannelRouter -> SampleToBytesProvider -> WasapiOut (X4)
///
/// Rebuild the router when the output device changes (channel count is fixed per instance).
/// </summary>
public sealed class ChannelRouter : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _outCh;
    private readonly int _srcCh;
    private readonly int _routedInputs;
    private readonly double _sampleRate;
    private readonly object _writeLock = new();

    private RoutingSnapshot _snapshot;

    // Per output DSP state (audio thread only).
    private readonly LowPass24[] _lowPass;
    private readonly double[] _appliedCutoff;
    private readonly float[] _gainCurrent;
    private readonly float[] _lpMixCurrent;
    private readonly HighPass24[] _highPass;
    private readonly double[] _appliedHighPass;
    private readonly float[] _hpMixCurrent;
    private readonly float[] _mixCurrent;   // smoothed effective weight: matrix * input level
    private readonly float[] _mixTarget;

    // Smoothing. One pole, ~10 ms. Kills zipper noise on slider drags and toggles.
    private readonly float _smooth;
    private float _gameMix = 1f;

    // Test tone state (audio thread only).
    private int _activeToneChannel = -1;
    private double _tonePhase;
    private float _toneEnv;
    private readonly float _toneSmooth;
    private long _pulseCounter;
    private readonly long _pulsePeriod;
    private readonly long _pulseOn;

    private float[] _inBuf = Array.Empty<float>();
    private readonly float[] _outPeakScratch;

    // Meters. Written by the audio thread once per buffer, polled by the UI (~30 Hz).
    private readonly float[] _inPeaks = new float[RoutingSnapshot.MaxInputs];
    private readonly float[] _outPeaks;

    public WaveFormat WaveFormat { get; }
    public int OutputChannels => _outCh;

    /// <param name="source">Captured game audio, IEEE float, interleaved, 8 slots.</param>
    /// <param name="outputFormat">Any format carrying the output channel count and sample rate.</param>
    public ChannelRouter(ISampleProvider source, WaveFormat outputFormat)
    {
        if (source.WaveFormat.SampleRate != outputFormat.SampleRate)
            throw new ArgumentException(
                $"Sample rate mismatch: source {source.WaveFormat.SampleRate} Hz, output {outputFormat.SampleRate} Hz.");

        _source = source;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(outputFormat.SampleRate, outputFormat.Channels);
        _outCh = outputFormat.Channels;
        _srcCh = source.WaveFormat.Channels;
        _routedInputs = Math.Min(_srcCh, RoutingSnapshot.MaxInputs);
        _sampleRate = outputFormat.SampleRate;

        _snapshot = RoutingSnapshot.CreateDefault(_outCh);
        _lowPass = new LowPass24[_outCh];
        _appliedCutoff = new double[_outCh];
        Array.Fill(_appliedCutoff, -1.0);
        _gainCurrent = new float[_outCh];
        _lpMixCurrent = new float[_outCh];
        _highPass = new HighPass24[_outCh];
        _appliedHighPass = new double[_outCh];
        Array.Fill(_appliedHighPass, -1.0);
        _hpMixCurrent = new float[_outCh];
        _mixCurrent = new float[RoutingSnapshot.MaxInputs * _outCh];
        _mixTarget = new float[RoutingSnapshot.MaxInputs * _outCh];
        _outPeaks = new float[_outCh];
        _outPeakScratch = new float[_outCh];

        for (int o = 0; o < _outCh; o++)
        {
            _gainCurrent[o] = _snapshot.Gains[o];
            _lpMixCurrent[o] = _snapshot.LowPassEnabled[o] ? 1f : 0f;
        }

        _smooth = OnePole(0.010);
        _toneSmooth = OnePole(0.015); // 15 ms fade so the tone starts and stops without a thump
        _pulsePeriod = (long)(_sampleRate * 0.7);
        _pulseOn = (long)(_sampleRate * 0.4);
    }

    // ------------------------------------------------------------------
    // UI-side API. Call from any thread. Each call publishes a new snapshot.
    // ------------------------------------------------------------------

    public RoutingSnapshot Current => Volatile.Read(ref _snapshot);

    public void SetRouteWeight(int input, int output, float weight) =>
        Mutate(s => s.Matrix[input * _outCh + output] = Math.Clamp(weight, 0f, 2f));

    public void SetInputGain(int input, float gain) =>
        Mutate(s => s.InputGains[input] = Math.Clamp(gain, 0f, 1f));

    public void SetGain(int output, float gain) =>
        Mutate(s => s.Gains[output] = Math.Clamp(gain, 0f, 2f));

    public void SetLowPass(int output, bool enabled) =>
        Mutate(s => s.LowPassEnabled[output] = enabled);

    public void SetCutoff(int output, double hz) =>
        Mutate(s => s.Cutoffs[output] = Math.Clamp(hz, 20.0, 250.0));

    public void SetHighPass(int output, bool enabled) =>
        Mutate(s => s.HighPassEnabled[output] = enabled);

    public void SetHighPassCutoff(int output, double hz) =>
        Mutate(s => s.HighPassCutoffs[output] = Math.Clamp(hz, 5.0, 80.0));

    /// <summary>Applies a transducer preset's filters to one output in a single atomic change.</summary>
    public void ApplyPreset(int output, string presetId, bool lowPass, double lowPassHz, bool highPass, double highPassHz) =>
        Mutate(s =>
        {
            s.PresetIds[output] = presetId;
            s.LowPassEnabled[output] = lowPass;
            s.Cutoffs[output] = Math.Clamp(lowPassHz, 20.0, 250.0);
            s.HighPassEnabled[output] = highPass;
            s.HighPassCutoffs[output] = Math.Clamp(highPassHz, 5.0, 80.0);
        });

    public void SetPresetId(int output, string presetId) =>
        Mutate(s => s.PresetIds[output] = presetId);

    public void SetMuted(int output, bool muted) =>
        Mutate(s => s.Muted[output] = muted);

    public void StartTestTone(int output, double hz, float level, bool pulsed) =>
        Replace(s => With(s, toneChannel: output, toneHz: Math.Clamp(hz, 20.0, 120.0),
                          toneLevel: Math.Clamp(level, 0f, 1f), pulsed: pulsed));

    public void StopTestTone() => Replace(s => With(s, toneChannel: -1));

    /// <summary>Load a whole profile in one atomic swap. Keeps the current test tone state.</summary>
    public void Load(RoutingSnapshot snapshot)
    {
        if (snapshot.OutputChannels != _outCh)
            throw new ArgumentException("Profile channel count does not match this output device.");

        lock (_writeLock)
        {
            var current = Volatile.Read(ref _snapshot);
            var next = snapshot.Clone();
            var merged = With(next, toneChannel: current.TestToneChannel, toneHz: current.TestToneHz,
                              toneLevel: current.TestToneLevel, pulsed: current.TestTonePulsed);
            Volatile.Write(ref _snapshot, merged);
        }
    }

    /// <summary>Copies the latest peak levels (linear 0..1) for meters.</summary>
    public void ReadPeaks(float[] inputs, float[] outputs)
    {
        for (int i = 0; i < inputs.Length && i < _inPeaks.Length; i++) inputs[i] = Volatile.Read(ref _inPeaks[i]);
        for (int o = 0; o < outputs.Length && o < _outPeaks.Length; o++) outputs[o] = Volatile.Read(ref _outPeaks[o]);
    }

    private void Mutate(Action<RoutingSnapshot> edit)
    {
        lock (_writeLock)
        {
            var next = Volatile.Read(ref _snapshot).Clone();
            edit(next);
            Volatile.Write(ref _snapshot, next);
        }
    }

    private void Replace(Func<RoutingSnapshot, RoutingSnapshot> build)
    {
        lock (_writeLock)
            Volatile.Write(ref _snapshot, build(Volatile.Read(ref _snapshot)));
    }

    private static RoutingSnapshot With(RoutingSnapshot s, int? toneChannel = null,
        double? toneHz = null, float? toneLevel = null, bool? pulsed = null) => new()
    {
        OutputChannels = s.OutputChannels,
        Matrix = (float[])s.Matrix.Clone(),
        Gains = (float[])s.Gains.Clone(),
        InputGains = (float[])s.InputGains.Clone(),
        LowPassEnabled = (bool[])s.LowPassEnabled.Clone(),
        Cutoffs = (double[])s.Cutoffs.Clone(),
        HighPassEnabled = (bool[])s.HighPassEnabled.Clone(),
        HighPassCutoffs = (double[])s.HighPassCutoffs.Clone(),
        PresetIds = (string[])s.PresetIds.Clone(),
        Muted = (bool[])s.Muted.Clone(),
        TestToneChannel = toneChannel ?? s.TestToneChannel,
        TestToneHz = toneHz ?? s.TestToneHz,
        TestToneLevel = toneLevel ?? s.TestToneLevel,
        TestTonePulsed = pulsed ?? s.TestTonePulsed,
        MuteGameDuringTest = s.MuteGameDuringTest,
    };

    // ------------------------------------------------------------------
    // Audio thread.
    // ------------------------------------------------------------------

    public int Read(float[] buffer, int offset, int count)
    {
        var s = Volatile.Read(ref _snapshot);
        int frames = count / _outCh;

        // Pull the matching number of input frames. Always fill: WasapiOut stops if we return short,
        // and the test tone must keep working when the game is not running.
        int needed = frames * _srcCh;
        if (_inBuf.Length < needed) _inBuf = new float[needed]; // rare; only when buffer size grows
        int got = _source.Read(_inBuf, 0, needed);
        if (got < needed) Array.Clear(_inBuf, Math.Max(got, 0), needed - Math.Max(got, 0));

        for (int o = 0; o < _outCh; o++)
        {
            if (s.Cutoffs[o] != _appliedCutoff[o])
            {
                _lowPass[o].Configure(_sampleRate, s.Cutoffs[o]);
                _appliedCutoff[o] = s.Cutoffs[o];
            }
            if (s.HighPassCutoffs[o] != _appliedHighPass[o])
            {
                _highPass[o].Configure(_sampleRate, s.HighPassCutoffs[o]);
                _appliedHighPass[o] = s.HighPassCutoffs[o];
            }
        }

        UpdateInputPeaks(frames);
        Array.Clear(_outPeakScratch, 0, _outCh);

        // Effective route weight = cell level * game channel level. Smoothed per sample below
        // so dragging a fader doesn't zipper.
        for (int i = 0; i < _routedInputs; i++)
        {
            float ig = s.InputGains[i];
            for (int o = 0; o < _outCh; o++)
                _mixTarget[i * _outCh + o] = s.Matrix[i * _outCh + o] * ig;
        }

        double phaseInc = 2.0 * Math.PI * s.TestToneHz / _sampleRate;
        float gameMixTarget = (s.TestToneChannel >= 0 && s.MuteGameDuringTest) ? 0f : 1f;

        for (int f = 0; f < frames; f++)
        {
            float tone = NextToneSample(s, phaseInc);
            _gameMix += (gameMixTarget - _gameMix) * _smooth;

            int ib = f * _srcCh;
            int ob = offset + f * _outCh;

            for (int o = 0; o < _outCh; o++)
            {
                // 1. Matrix sum
                float sum = 0f;
                for (int i = 0; i < _routedInputs; i++)
                {
                    int k = i * _outCh + o;
                    _mixCurrent[k] += (_mixTarget[k] - _mixCurrent[k]) * _smooth;
                    sum += _inBuf[ib + i] * _mixCurrent[k];
                }

                sum *= _gameMix;
                if (o == _activeToneChannel) sum += tone;

                // 2. Low pass. Filter always runs so its state is warm; toggling crossfades dry/wet.
                float wet = (float)_lowPass[o].Process(sum);
                float lpTarget = s.LowPassEnabled[o] ? 1f : 0f;
                _lpMixCurrent[o] += (lpTarget - _lpMixCurrent[o]) * _smooth;
                float y = sum + (wet - sum) * _lpMixCurrent[o];

                // 2b. High pass (subsonic). Same always-running, crossfaded approach.
                float hpWet = (float)_highPass[o].Process(y);
                float hpTarget = s.HighPassEnabled[o] ? 1f : 0f;
                _hpMixCurrent[o] += (hpTarget - _hpMixCurrent[o]) * _smooth;
                y += (hpWet - y) * _hpMixCurrent[o];

                // 3. Gain (0..2x), smoothed
                float gainTarget = s.Muted[o] ? 0f : s.Gains[o];
                _gainCurrent[o] += (gainTarget - _gainCurrent[o]) * _smooth;
                y *= _gainCurrent[o];

                // 4. Soft clip. Summing 3 channels at 2x will exceed 0 dBFS; hard clipping
                //    on a transducer feels like a harsh buzz.
                y = SoftClip(y);

                buffer[ob + o] = y;
                float a = MathF.Abs(y);
                if (a > _outPeakScratch[o]) _outPeakScratch[o] = a;
            }
        }

        for (int o = 0; o < _outCh; o++) Volatile.Write(ref _outPeaks[o], _outPeakScratch[o]);
        return frames * _outCh;
    }

    private float NextToneSample(RoutingSnapshot s, double phaseInc)
    {
        // Fade out the old channel fully before moving the tone to a new one.
        float envTarget;
        if (s.TestToneChannel < 0 || s.TestToneChannel != _activeToneChannel)
        {
            envTarget = 0f;
        }
        else
        {
            bool gateOpen = !s.TestTonePulsed || (_pulseCounter % _pulsePeriod) < _pulseOn;
            envTarget = gateOpen ? 1f : 0f;
            _pulseCounter++;
        }

        _toneEnv += (envTarget - _toneEnv) * _toneSmooth;

        if (_activeToneChannel != s.TestToneChannel && _toneEnv < 1e-4f)
        {
            _activeToneChannel = s.TestToneChannel < _outCh ? s.TestToneChannel : -1;
            _tonePhase = 0;
            _pulseCounter = 0;
        }

        if (_activeToneChannel < 0) return 0f;

        float sample = (float)Math.Sin(_tonePhase) * _toneEnv * s.TestToneLevel;
        _tonePhase += phaseInc;
        if (_tonePhase >= 2.0 * Math.PI) _tonePhase -= 2.0 * Math.PI;
        return sample;
    }

    private void UpdateInputPeaks(int frames)
    {
        for (int i = 0; i < _routedInputs; i++)
        {
            float pk = 0f;
            for (int f = 0; f < frames; f++)
            {
                float a = MathF.Abs(_inBuf[f * _srcCh + i]);
                if (a > pk) pk = a;
            }
            Volatile.Write(ref _inPeaks[i], pk);
        }
    }

    private static float SoftClip(float x)
    {
        const float knee = 0.8f;
        float a = MathF.Abs(x);
        if (a <= knee) return x;
        float shaped = knee + (1f - knee) * MathF.Tanh((a - knee) / (1f - knee));
        return MathF.CopySign(shaped, x);
    }

    private float OnePole(double seconds) => (float)(1.0 - Math.Exp(-1.0 / (seconds * _sampleRate)));
}
