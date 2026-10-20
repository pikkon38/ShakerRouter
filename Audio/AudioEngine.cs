using System;
using System.Collections.Generic;
using System.Reflection;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ShakerRouter.Audio;

/// <summary>
/// Owns the whole signal chain:
///   ProcessLoopbackCapture -> BufferedWaveProvider -> ChannelRouter -> SampleToBytesProvider -> WasapiOut
///
/// The capture runs at the output device's sample rate (Windows resamples inside the loopback),
/// so no resampler is needed in our chain.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private WasapiOut? _out;
    private ProcessLoopbackCapture? _capture;
    private BufferedWaveProvider? _buffer;

    public ChannelRouter? Router { get; private set; }
    public bool IsRunning => _out != null;
    public string OutputFormatDescription { get; private set; } = string.Empty;
    public string NegotiatedCaptureLayout => _capture?.NegotiatedLayout ?? string.Empty;

    /// <summary>Raised from the capture thread.</summary>
    public event Action<string>? CaptureStatusChanged;

    /// <summary>Raised from an audio thread when playback stops on its own (device unplugged, etc).</summary>
    public event Action<string>? OutputStopped;

    public void Start(string deviceId, AudioClientShareMode shareMode, string processName)
    {
        Stop();

        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDevice(deviceId);

        var (format, kind) = ChooseOutputFormat(device, shareMode);

        _buffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, ProcessLoopbackCapture.Slots))
        {
            BufferDuration = TimeSpan.FromSeconds(1),
            DiscardOnBufferOverflow = true,
            ReadFully = true,
        };

        Router = new ChannelRouter(_buffer.ToSampleProvider(), format);
        var bytes = new SampleToBytesProvider(Router, format, kind);

        int latencyMs = shareMode == AudioClientShareMode.Shared ? 30 : 20;
        _out = new WasapiOut(device, shareMode, true, latencyMs);
        _out.PlaybackStopped += OnPlaybackStopped;
        _out.Init(bytes);
        _out.Play();

        OutputFormatDescription = $"{format.Channels}ch {format.SampleRate / 1000.0:0.#} kHz {KindName(kind)} {(shareMode == AudioClientShareMode.Shared ? "shared" : "exclusive")}";

        _capture = new ProcessLoopbackCapture(_buffer, processName);
        _capture.StatusChanged += s => CaptureStatusChanged?.Invoke(s);
        _capture.Start();
    }

    public void SetTargetProcess(string processName)
    {
        if (_capture != null) _capture.ProcessName = processName;
    }

    public void Stop()
    {
        _capture?.Dispose();
        _capture = null;

        if (_out != null)
        {
            _out.PlaybackStopped -= OnPlaybackStopped;
            try { _out.Stop(); } catch { /* device may already be gone */ }
            _out.Dispose();
            _out = null;
        }

        _buffer = null;
        Router = null;
        OutputFormatDescription = string.Empty;
    }

    public void Dispose() => Stop();

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        string reason = e.Exception?.Message ?? "Playback stopped";
        OutputStopped?.Invoke(reason);
    }

    // ------------------------------------------------------------------
    // Format negotiation
    // ------------------------------------------------------------------

    private static (WaveFormat Format, OutputSampleKind Kind) ChooseOutputFormat(MMDevice device, AudioClientShareMode shareMode)
    {
        var mix = device.AudioClient.MixFormat;

        if (shareMode == AudioClientShareMode.Shared)
        {
            // Shared mode: hand Windows exactly its own mix format. Always accepted, no conversion.
            var kind = mix.BitsPerSample switch
            {
                32 => OutputSampleKind.Float32,
                24 => OutputSampleKind.Int24,
                _ => OutputSampleKind.Int16,
            };
            return (mix, kind);
        }

        // Exclusive mode: find something the hardware takes natively.
        uint mask = DeviceFormatInfo.ReadChannelMask(mix);
        var rates = new List<int> { mix.SampleRate };
        if (!rates.Contains(48000)) rates.Add(48000);
        if (!rates.Contains(44100)) rates.Add(44100);

        var kinds = new[] { OutputSampleKind.Float32, OutputSampleKind.Int32, OutputSampleKind.Int24, OutputSampleKind.Int16 };

        foreach (int rate in rates)
        {
            foreach (var kind in kinds)
            {
                var candidate = DeviceFormatInfo.CreateExtensible(rate, mix.Channels, mask, kind);
                if (candidate == null) continue;
                bool supported;
                try { supported = device.AudioClient.IsFormatSupported(AudioClientShareMode.Exclusive, candidate); }
                catch { supported = false; }
                if (supported) return (candidate, kind);
            }
        }

        throw new NotSupportedException("The device rejected every exclusive-mode format. Use Shared mode.");
    }

    private static string KindName(OutputSampleKind kind) => kind switch
    {
        OutputSampleKind.Float32 => "float",
        OutputSampleKind.Int32 => "32-bit",
        OutputSampleKind.Int24 => "24-bit",
        _ => "16-bit",
    };
}

/// <summary>Helpers for reading and building WAVEFORMATEXTENSIBLE through NAudio's types.</summary>
public static class DeviceFormatInfo
{
    private static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");

    private static readonly FieldInfo? MaskField =
        typeof(WaveFormatExtensible).GetField("dwChannelMask", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo? ValidBitsField =
        typeof(WaveFormatExtensible).GetField("wValidBitsPerSample", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo? SubFormatField =
        typeof(WaveFormatExtensible).GetField("subFormat", BindingFlags.NonPublic | BindingFlags.Instance);

    /// <summary>Speaker mask of a device format. Falls back to the standard mask for the channel count.</summary>
    public static uint ReadChannelMask(WaveFormat format)
    {
        if (format is WaveFormatExtensible ext && MaskField?.GetValue(ext) is int raw && raw != 0)
            return unchecked((uint)raw);
        return DefaultMask(format.Channels);
    }

    public static uint DefaultMask(int channels) => channels switch
    {
        1 => 0x4,
        2 => 0x3,
        4 => 0x33,
        6 => 0x60F,
        8 => 0x63F,
        _ => channels >= 32 ? 0xFFFFFFFF : (1u << channels) - 1,
    };

    /// <summary>Builds an extensible format with a proper speaker mask. Returns null if NAudio internals changed.</summary>
    public static WaveFormatExtensible? CreateExtensible(int rate, int channels, uint mask, OutputSampleKind kind)
    {
        int bits = kind switch
        {
            OutputSampleKind.Float32 => 32,
            OutputSampleKind.Int32 => 32,
            OutputSampleKind.Int24 => 24,
            _ => 16,
        };

        // NAudio's ctor picks IEEE float for 32 bits and PCM otherwise, and a naive (1<<n)-1 mask.
        var f = new WaveFormatExtensible(rate, bits, channels);

        if (MaskField != null) MaskField.SetValue(f, unchecked((int)mask));

        if (kind == OutputSampleKind.Int32)
        {
            // 32-bit integer container: switch subformat to PCM. Most USB DACs report 24 valid bits.
            if (SubFormatField == null || ValidBitsField == null) return null;
            SubFormatField.SetValue(f, SubtypePcm);
            ValidBitsField.SetValue(f, (short)24);
        }

        return f;
    }
}
