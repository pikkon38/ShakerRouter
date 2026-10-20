using System.Collections.Generic;

namespace ShakerRouter.Models;

/// <summary>What the game is rendering, which follows the Windows default device's speaker setup.</summary>
public enum SourceLayout
{
    Stereo,
    Surround51,
    Surround71,
}

public static class SourceLayoutNames
{
    public static string Display(SourceLayout layout) => layout switch
    {
        SourceLayout.Stereo => "Stereo",
        SourceLayout.Surround51 => "5.1",
        _ => "7.1",
    };

    public static SourceLayout FromChannelCount(int channels) =>
        channels >= 8 ? SourceLayout.Surround71 :
        channels >= 4 ? SourceLayout.Surround51 :
        SourceLayout.Stereo;
}

public sealed class OutputDeviceInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required int Channels { get; init; }
    public required int SampleRate { get; init; }
    public required uint ChannelMask { get; init; }
    public bool IsDefault { get; init; }

    public string Display => $"{Name}  ({Channels}ch{(IsDefault ? ", Windows default" : "")})";
    public override string ToString() => Display;
}

public sealed class DefaultDeviceInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required int Channels { get; init; }
    public SourceLayout Layout => SourceLayoutNames.FromChannelCount(Channels);
}

/// <summary>Persisted routing for one output device and one source layout.</summary>
public sealed class RoutingProfileDto
{
    public int OutputChannels { get; set; }
    public float[] Matrix { get; set; } = System.Array.Empty<float>();
    public float[] Gains { get; set; } = System.Array.Empty<float>();
    public float[]? InputGains { get; set; }
    public bool[] LowPass { get; set; } = System.Array.Empty<bool>();
    public double[] Cutoffs { get; set; } = System.Array.Empty<double>();
    public bool[] Muted { get; set; } = System.Array.Empty<bool>();
}

public sealed class AppSettings
{
    public string? OutputDeviceId { get; set; }
    public string ShareMode { get; set; } = "Shared";
    public string TargetProcess { get; set; } = "StarCitizen";
    public string LayoutOverride { get; set; } = "Auto";
    public bool Enabled { get; set; } = true;
    public double TestToneHz { get; set; } = 45;
    public double TestToneLevel { get; set; } = 50;
    public bool TestTonePulsed { get; set; } = true;

    /// <summary>Key: "{deviceId}|{SourceLayout}".</summary>
    public Dictionary<string, RoutingProfileDto> Profiles { get; set; } = new();
}
