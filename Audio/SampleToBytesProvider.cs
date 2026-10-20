using System;
using NAudio.Wave;

namespace ShakerRouter.Audio;

public enum OutputSampleKind
{
    Float32,
    Int32,   // 32-bit container (24 valid bits on most USB DACs)
    Int24,   // packed 3 bytes
    Int16,
}

/// <summary>
/// Turns the router's float output into the exact byte format the output device accepted.
/// NAudio's SampleToWaveProvider refuses WAVE_FORMAT_EXTENSIBLE, and exclusive mode often
/// needs integer PCM, so this handles both.
/// </summary>
public sealed class SampleToBytesProvider : IWaveProvider
{
    private readonly ISampleProvider _source;
    private readonly OutputSampleKind _kind;
    private readonly int _bytesPerSample;
    private float[] _floats = Array.Empty<float>();

    public WaveFormat WaveFormat { get; }

    public SampleToBytesProvider(ISampleProvider source, WaveFormat outputFormat, OutputSampleKind kind)
    {
        if (source.WaveFormat.Channels != outputFormat.Channels || source.WaveFormat.SampleRate != outputFormat.SampleRate)
            throw new ArgumentException("Source and output must share channel count and sample rate.");

        _source = source;
        _kind = kind;
        WaveFormat = outputFormat;
        _bytesPerSample = kind switch
        {
            OutputSampleKind.Float32 => 4,
            OutputSampleKind.Int32 => 4,
            OutputSampleKind.Int24 => 3,
            _ => 2,
        };
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        int samples = count / _bytesPerSample;
        samples -= samples % WaveFormat.Channels;
        if (_floats.Length < samples) _floats = new float[samples];

        int read = _source.Read(_floats, 0, samples);
        if (read < samples) Array.Clear(_floats, read, samples - read);

        switch (_kind)
        {
            case OutputSampleKind.Float32:
                Buffer.BlockCopy(_floats, 0, buffer, offset, samples * 4);
                break;

            case OutputSampleKind.Int32:
                for (int i = 0; i < samples; i++)
                {
                    int v = (int)(Math.Clamp(_floats[i], -1f, 1f) * 2147483647.0);
                    int o = offset + i * 4;
                    buffer[o] = (byte)v;
                    buffer[o + 1] = (byte)(v >> 8);
                    buffer[o + 2] = (byte)(v >> 16);
                    buffer[o + 3] = (byte)(v >> 24);
                }
                break;

            case OutputSampleKind.Int24:
                for (int i = 0; i < samples; i++)
                {
                    int v = (int)(Math.Clamp(_floats[i], -1f, 1f) * 8388607f);
                    int o = offset + i * 3;
                    buffer[o] = (byte)v;
                    buffer[o + 1] = (byte)(v >> 8);
                    buffer[o + 2] = (byte)(v >> 16);
                }
                break;

            default:
                for (int i = 0; i < samples; i++)
                {
                    short v = (short)(Math.Clamp(_floats[i], -1f, 1f) * 32767f);
                    int o = offset + i * 2;
                    buffer[o] = (byte)v;
                    buffer[o + 1] = (byte)(v >> 8);
                }
                break;
        }

        return samples * _bytesPerSample;
    }
}
