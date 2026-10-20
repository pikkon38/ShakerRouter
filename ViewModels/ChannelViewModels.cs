using System;
using System.Collections.ObjectModel;
using System.Windows.Input;
using ShakerRouter.Mvvm;

namespace ShakerRouter.ViewModels;

/// <summary>One physical output on the selected device (one transducer).</summary>
public sealed class OutputChannelViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private double _gainPercent = 100;
    private bool _bassOnly = true;
    private double _cutoffHz = 80;
    private bool _muted;
    private double _level;
    private bool _isTesting;

    public OutputChannelViewModel(MainViewModel owner, int index, string name)
    {
        _owner = owner;
        Index = index;
        Name = name;
        TestCommand = new RelayCommand(() => _owner.ToggleTest(Index));
    }

    public int Index { get; }
    public string Name { get; }
    public string Label => $"Out {Index + 1}";
    public ICommand TestCommand { get; }

    /// <summary>0 to 200 percent.</summary>
    public double GainPercent
    {
        get => _gainPercent;
        set
        {
            if (Set(ref _gainPercent, Math.Clamp(value, 0, 200)))
                _owner.OnGainChanged(Index, _gainPercent);
        }
    }

    public bool BassOnly
    {
        get => _bassOnly;
        set
        {
            if (Set(ref _bassOnly, value))
                _owner.OnBassOnlyChanged(Index, value);
        }
    }

    public double CutoffHz
    {
        get => _cutoffHz;
        set
        {
            if (Set(ref _cutoffHz, Math.Round(Math.Clamp(value, 30, 200))))
                _owner.OnCutoffChanged(Index, _cutoffHz);
        }
    }

    public bool Muted
    {
        get => _muted;
        set
        {
            if (Set(ref _muted, value))
                _owner.OnMutedChanged(Index, value);
        }
    }

    /// <summary>Meter, 0..1 on a -48 dB to 0 dB scale.</summary>
    public double Level
    {
        get => _level;
        set => Set(ref _level, value);
    }

    public bool IsTesting
    {
        get => _isTesting;
        set => Set(ref _isTesting, value);
    }

    /// <summary>Set from a loaded profile without pushing back to the router.</summary>
    internal void Sync(double gainPercent, bool bassOnly, double cutoff, bool muted)
    {
        Set(ref _gainPercent, gainPercent, nameof(GainPercent));
        Set(ref _bassOnly, bassOnly, nameof(BassOnly));
        Set(ref _cutoffHz, cutoff, nameof(CutoffHz));
        Set(ref _muted, muted, nameof(Muted));
    }
}

/// <summary>One game channel (matrix row).</summary>
public sealed class InputRowViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private double _level;
    private double _percent = 100;

    public InputRowViewModel(MainViewModel owner, int index, string name, string description)
    {
        _owner = owner;
        Index = index;
        Name = name;
        Description = description;
    }

    public int Index { get; }
    public string Name { get; }
    public string Description { get; }
    public ObservableCollection<MatrixCellViewModel> Cells { get; } = new();

    /// <summary>Meter, 0..1.</summary>
    public double Level
    {
        get => _level;
        set => Set(ref _level, value);
    }

    /// <summary>Channel level, 0 to 100 percent. Scales this game channel on every output.</summary>
    public double Percent
    {
        get => _percent;
        set
        {
            if (Set(ref _percent, Math.Round(Math.Clamp(value, 0, 100))))
                _owner.OnInputGainChanged(Index, _percent);
        }
    }

    internal void Sync(double percent) => Set(ref _percent, Math.Round(percent), nameof(Percent));
}

/// <summary>One route: game input row to device output column, 0 to 100 percent.</summary>
public sealed class MatrixCellViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private double _percent;
    private double _lastOnPercent = 100;

    public MatrixCellViewModel(MainViewModel owner, int input, int output)
    {
        _owner = owner;
        InputIndex = input;
        OutputIndex = output;
    }

    public int InputIndex { get; }
    public int OutputIndex { get; }

    public double Percent
    {
        get => _percent;
        set
        {
            value = Math.Round(Math.Clamp(value, 0, 100));
            if (!Set(ref _percent, value)) return;
            if (value > 0) _lastOnPercent = value;
            OnPropertyChanged(nameof(IsOn));
            OnPropertyChanged(nameof(Text));
            _owner.OnRouteChanged(InputIndex, OutputIndex, (float)(value / 100.0));
        }
    }

    public bool IsOn => _percent > 0;
    public string Text => _percent <= 0 ? "off" : $"{_percent:0}%";

    /// <summary>Right click: off, or back to the last level it had.</summary>
    public void Toggle() => Percent = _percent > 0 ? 0 : _lastOnPercent;

    /// <summary>Mouse wheel: 5% steps, 1% with Shift.</summary>
    public void Nudge(int steps, bool fine) => Percent = _percent + steps * (fine ? 1 : 5);

    internal void Sync(float weight)
    {
        _percent = Math.Round(Math.Clamp(weight * 100.0, 0, 100));
        if (_percent > 0) _lastOnPercent = _percent;
        OnPropertyChanged(nameof(Percent));
        OnPropertyChanged(nameof(IsOn));
        OnPropertyChanged(nameof(Text));
    }
}
