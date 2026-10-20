using System.Collections.Generic;
using System.Linq;

namespace ShakerRouter.Services;

/// <summary>
/// A starting point for one transducer model: which filters to use and where.
/// Specs come from the manufacturers' published data. Picking a preset sets the
/// output's filters; gain is left alone because safe gain depends on your amp.
/// </summary>
public sealed class TransducerPreset
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>Published spec line shown under the dropdown.</summary>
    public required string Specs { get; init; }

    /// <summary>One line on what it's good for and why these settings.</summary>
    public required string Notes { get; init; }

    public bool LowPass { get; init; } = true;
    public double LowPassHz { get; init; } = 80;
    public bool HighPass { get; init; }
    public double HighPassHz { get; init; } = 20;

    public override string ToString() => Name;
}

public static class TransducerPresets
{
    public const string CustomId = "";

    public static readonly TransducerPreset Custom = new()
    {
        Id = CustomId,
        Name = "Custom",
        Specs = "No preset. Set the filters yourself.",
        Notes = string.Empty,
    };

    public static readonly IReadOnlyList<TransducerPreset> All = new List<TransducerPreset>
    {
        Custom,

        // ---- Dayton Audio ----
        new()
        {
            Id = "dayton-tt25",
            Name = "Dayton TT25 Puck",
            Specs = "20-80 Hz · Fs 40 Hz · 15 W RMS · 8 or 16 Ω",
            Notes = "Small puck for texture. High pass at 20 Hz keeps its 15 W on what you can feel. Keep gain modest.",
            LowPassHz = 80, HighPass = true, HighPassHz = 20,
        },
        new()
        {
            Id = "dayton-bst1",
            Name = "Dayton BST-1",
            Specs = "10-80 Hz · Fs 30 Hz · 50 W RMS · 4 Ω",
            Notes = "Deep rumble for the seat. Reaches 10 Hz, so no high pass.",
            LowPassHz = 65,
        },
        new()
        {
            Id = "dayton-bst300ex",
            Name = "Dayton BST-300EX",
            Specs = "10-200 Hz · Fs 31 Hz · 300 W RMS · 4 Ω",
            Notes = "Heavy-duty shaker. Dayton recommends a low pass at 80 Hz or below.",
            LowPassHz = 80,
        },

        // ---- AuraSound ----
        new()
        {
            Id = "aura-ast2b4",
            Name = "Aura AST-2B-4 Pro",
            Specs = "20-80 Hz · Fs 40 Hz · 50 W RMS · 4 Ω",
            Notes = "Mid-size shaker. Rated from 20 Hz, so a 20 Hz high pass saves power.",
            LowPassHz = 80, HighPass = true, HighPassHz = 20,
        },

        // ---- ButtKicker ----
        new()
        {
            Id = "bk-gamer",
            Name = "ButtKicker Gamer 2",
            Specs = "5-200 Hz · 2 Ω · up to 250 W · own amp",
            Notes = "Chair-mounted gaming kicker. 100 Hz keeps punchy hits without dialogue.",
            LowPassHz = 100,
        },
        new()
        {
            Id = "bk-mini-lfe",
            Name = "ButtKicker Mini LFE",
            Specs = "10-200 Hz · 4 Ω · 50-250 W",
            Notes = "Compact kicker for seats and rigs. Needs at least 50 W.",
            LowPassHz = 100,
        },
        new()
        {
            Id = "bk-advance",
            Name = "ButtKicker Advance",
            Specs = "5-200 Hz · 4 Ω · 75-400 W",
            Notes = "Mid-power kicker. Strong into the single digits, so no high pass.",
            LowPassHz = 80,
        },
        new()
        {
            Id = "bk-lfe",
            Name = "ButtKicker LFE",
            Specs = "5-200 Hz · 4 Ω · 400-1500 W",
            Notes = "Full-size kicker for platforms and couches. Pure rumble at 80 Hz.",
            LowPassHz = 80,
        },

        // ---- Clark Synthesis ----
        new()
        {
            Id = "clark-tst239",
            Name = "Clark TST239 Silver",
            Specs = "15-800 Hz tactile · 4 Ω · 100 W RMS",
            Notes = "Wide-range transducer with fine detail. 100 Hz stays below most dialogue. Raise it for more texture.",
            LowPassHz = 100, HighPass = true, HighPassHz = 15,
        },
    };

    public static TransducerPreset Find(string? id) =>
        All.FirstOrDefault(p => p.Id == (id ?? CustomId)) ?? Custom;
}
