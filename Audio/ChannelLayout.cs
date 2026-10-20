using System;
using System.Collections.Generic;

namespace ShakerRouter.Audio;

/// <summary>
/// WAVEFORMATEXTENSIBLE dwChannelMask bits. Interleaved channel order in a stream
/// always follows ascending bit order, so the mask tells you what each column is.
/// </summary>
[Flags]
public enum SpeakerMask : uint
{
    FrontLeft          = 0x1,
    FrontRight         = 0x2,
    FrontCenter        = 0x4,
    LowFrequency       = 0x8,
    BackLeft           = 0x10,
    BackRight          = 0x20,
    FrontLeftOfCenter  = 0x40,
    FrontRightOfCenter = 0x80,
    BackCenter         = 0x100,
    SideLeft           = 0x200,
    SideRight          = 0x400,
    TopCenter          = 0x800,
    TopFrontLeft       = 0x1000,
    TopFrontCenter     = 0x2000,
    TopFrontRight      = 0x4000,
    TopBackLeft        = 0x8000,
    TopBackCenter      = 0x10000,
    TopBackRight       = 0x20000,
}

public static class ChannelLayout
{
    /// <summary>Standard 7.1 order (KSAUDIO_SPEAKER_7POINT1_SURROUND). These are the matrix rows.</summary>
    public static readonly string[] GameInputNames = { "FL", "FR", "C", "LFE", "RL", "RR", "SL", "SR" };

    public const uint Mask7Point1Surround =
        (uint)(SpeakerMask.FrontLeft | SpeakerMask.FrontRight | SpeakerMask.FrontCenter |
               SpeakerMask.LowFrequency | SpeakerMask.BackLeft | SpeakerMask.BackRight |
               SpeakerMask.SideLeft | SpeakerMask.SideRight);

    private static readonly Dictionary<SpeakerMask, string> ShortNames = new()
    {
        [SpeakerMask.FrontLeft] = "FL",
        [SpeakerMask.FrontRight] = "FR",
        [SpeakerMask.FrontCenter] = "C",
        [SpeakerMask.LowFrequency] = "LFE",
        [SpeakerMask.BackLeft] = "RL",
        [SpeakerMask.BackRight] = "RR",
        [SpeakerMask.FrontLeftOfCenter] = "FLC",
        [SpeakerMask.FrontRightOfCenter] = "FRC",
        [SpeakerMask.BackCenter] = "BC",
        [SpeakerMask.SideLeft] = "SL",
        [SpeakerMask.SideRight] = "SR",
        [SpeakerMask.TopCenter] = "TC",
        [SpeakerMask.TopFrontLeft] = "TFL",
        [SpeakerMask.TopFrontCenter] = "TFC",
        [SpeakerMask.TopFrontRight] = "TFR",
        [SpeakerMask.TopBackLeft] = "TBL",
        [SpeakerMask.TopBackCenter] = "TBC",
        [SpeakerMask.TopBackRight] = "TBR",
    };

    /// <summary>
    /// Builds the matrix column headers for an output device.
    /// Get the mask from the device mix format (WAVEFORMATEXTENSIBLE.dwChannelMask) or the
    /// endpoint property PKEY_AudioEndpoint_PhysicalSpeakers. Channels beyond the mask
    /// (or devices that report mask 0) fall back to "Out N".
    /// </summary>
    public static IReadOnlyList<string> OutputNames(uint channelMask, int channelCount)
    {
        var names = new List<string>(channelCount);
        for (int bit = 0; bit < 32 && names.Count < channelCount; bit++)
        {
            var flag = (SpeakerMask)(1u << bit);
            if ((channelMask & (uint)flag) == 0) continue;
            names.Add(ShortNames.TryGetValue(flag, out var n) ? n : $"Bit{bit}");
        }
        while (names.Count < channelCount)
            names.Add($"Out {names.Count + 1}");
        return names;
    }
}
