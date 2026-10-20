using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using NAudio.CoreAudioApi;
using ShakerRouter.Audio;
using ShakerRouter.Models;

namespace ShakerRouter.Services;

public static class DeviceService
{
    public static List<OutputDeviceInfo> ListOutputs()
    {
        var list = new List<OutputDeviceInfo>();
        using var enumerator = new MMDeviceEnumerator();

        string defaultId = string.Empty;
        try { defaultId = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console).ID; } catch { }

        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            try
            {
                var mix = device.AudioClient.MixFormat;
                list.Add(new OutputDeviceInfo
                {
                    Id = device.ID,
                    Name = device.FriendlyName,
                    Channels = mix.Channels,
                    SampleRate = mix.SampleRate,
                    ChannelMask = DeviceFormatInfo.ReadChannelMask(mix),
                    IsDefault = device.ID == defaultId,
                });
            }
            catch
            {
                // Device in a weird state (exclusive-locked by another app, driver issue). Skip it.
            }
        }

        return list.OrderBy(d => d.IsDefault).ThenBy(d => d.Name).ToList();
    }

    /// <summary>The device games render to. Its channel count decides what Star Citizen outputs.</summary>
    public static DefaultDeviceInfo? GetDefault()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            return new DefaultDeviceInfo
            {
                Id = device.ID,
                Name = device.FriendlyName,
                Channels = device.AudioClient.MixFormat.Channels,
            };
        }
        catch
        {
            return null;
        }
    }
}

public static class ProcessService
{
    public const string DefaultTarget = "StarCitizen";

    /// <summary>Windowed processes, sorted, with Star Citizen pinned first even when not running.</summary>
    public static List<string> ListCandidates()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int self = Environment.ProcessId;

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id != self && p.MainWindowHandle != IntPtr.Zero)
                    names.Add(p.ProcessName);
            }
            catch
            {
                // access denied on some system processes
            }
            finally
            {
                p.Dispose();
            }
        }

        names.Remove(DefaultTarget);
        var list = names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        list.Insert(0, DefaultTarget);
        return list;
    }
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;

    public SettingsStore()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShakerRouter");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");
    }

    public string FilePath => _path;

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Options) ?? new AppSettings();
        }
        catch
        {
            // Corrupt file: back it up and start fresh rather than crash.
            try { File.Copy(_path, _path + ".bad", true); } catch { }
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        try
        {
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
            File.Move(tmp, _path, true);
        }
        catch
        {
            // Non-fatal. Settings just won't persist this time.
        }
    }

    public static string ProfileKey(string deviceId, SourceLayout layout) => $"{deviceId}|{layout}";

    public static RoutingProfileDto ToDto(RoutingSnapshot s) => new()
    {
        OutputChannels = s.OutputChannels,
        Matrix = (float[])s.Matrix.Clone(),
        Gains = (float[])s.Gains.Clone(),
        InputGains = (float[])s.InputGains.Clone(),
        LowPass = (bool[])s.LowPassEnabled.Clone(),
        Cutoffs = (double[])s.Cutoffs.Clone(),
        Muted = (bool[])s.Muted.Clone(),
        HighPass = (bool[])s.HighPassEnabled.Clone(),
        HighPassCutoffs = (double[])s.HighPassCutoffs.Clone(),
        PresetIds = (string[])s.PresetIds.Clone(),
    };

    private static T[] Filled<T>(int n, T value)
    {
        var a = new T[n];
        Array.Fill(a, value);
        return a;
    }

    /// <summary>Returns null if the stored profile doesn't fit the device anymore.</summary>
    public static RoutingSnapshot? FromDto(RoutingProfileDto d, int outputChannels)
    {
        int n = outputChannels;
        if (d.OutputChannels != n) return null;
        if (d.Matrix.Length != RoutingSnapshot.MaxInputs * n || d.Gains.Length != n ||
            d.LowPass.Length != n || d.Cutoffs.Length != n || d.Muted.Length != n)
            return null;

        // Profiles saved before per-channel levels existed have no InputGains: default to 100%.
        float[] inputGains;
        if (d.InputGains != null && d.InputGains.Length == RoutingSnapshot.MaxInputs)
        {
            inputGains = (float[])d.InputGains.Clone();
        }
        else
        {
            inputGains = new float[RoutingSnapshot.MaxInputs];
            Array.Fill(inputGains, 1f);
        }

        return new RoutingSnapshot
        {
            OutputChannels = n,
            Matrix = (float[])d.Matrix.Clone(),
            Gains = (float[])d.Gains.Clone(),
            InputGains = inputGains,
            LowPassEnabled = (bool[])d.LowPass.Clone(),
            Cutoffs = (double[])d.Cutoffs.Clone(),
            Muted = (bool[])d.Muted.Clone(),
            HighPassEnabled = d.HighPass is { } hp && hp.Length == n ? (bool[])hp.Clone() : new bool[n],
            HighPassCutoffs = d.HighPassCutoffs is { } hc && hc.Length == n ? (double[])hc.Clone() : Filled(n, RoutingSnapshot.DefaultHighPassHz),
            PresetIds = d.PresetIds is { } ids && ids.Length == n ? Array.ConvertAll(ids, x => x ?? string.Empty) : Filled(n, string.Empty),
        };
    }
}

/// <summary>
/// Starting routes per source layout. Assumes the first three output jacks feed the
/// left, right and center transducers. Use the test tones to confirm, then remap.
/// </summary>
public static class DefaultProfiles
{
    // Input rows (fixed slot order from the capture).
    private const int FL = 0, FR = 1, C = 2, LFE = 3, RL = 4, RR = 5, SL = 6, SR = 7;

    public static RoutingSnapshot Create(SourceLayout layout, int outputChannels)
    {
        var s = RoutingSnapshot.CreateDefault(outputChannels);
        if (outputChannels < 1) return s;

        int left = 0;
        int right = outputChannels >= 2 ? 1 : 0;
        int center = outputChannels >= 3 ? 2 : -1;

        void Route(int input, int output, float weight)
        {
            if (output >= 0) s.Matrix[input * outputChannels + output] = weight;
        }

        switch (layout)
        {
            case SourceLayout.Surround71:
                // Sides carry guns and impacts; rears carry engines.
                Route(FL, left, 1f); Route(SL, left, 1f);
                Route(FR, right, 1f); Route(SR, right, 1f);
                Route(RL, center, 1f); Route(RR, center, 1f); Route(LFE, center, 1f);
                break;

            case SourceLayout.Surround51:
                // 5.1 has one surround pair. Engines and side guns share it, so the center gets half of each.
                Route(FL, left, 1f); Route(SL, left, 1f);
                Route(FR, right, 1f); Route(SR, right, 1f);
                Route(LFE, center, 1f); Route(SL, center, 0.5f); Route(SR, center, 0.5f);
                break;

            default:
                // Stereo: left/right only. Center gets a mono sum.
                Route(FL, left, 1f);
                Route(FR, right, 1f);
                Route(FL, center, 0.5f); Route(FR, center, 0.5f);
                break;
        }

        if (center >= 0) s.Cutoffs[center] = 60.0; // keep the seat on deep rumble only
        return s;
    }
}
