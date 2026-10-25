using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using ShakerRouter.Audio;
using ShakerRouter.Models;
using ShakerRouter.Mvvm;
using ShakerRouter.Services;

namespace ShakerRouter.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private static readonly string[] InputDescriptions =
    {
        "Front Left", "Front Right", "Center", "Subwoofer (LFE)",
        "Rear Left", "Rear Right", "Side Left", "Side Right",
    };

    private readonly Dispatcher _dispatcher;
    private readonly SettingsStore _store = new();
    private readonly AppSettings _settings;
    private readonly AudioEngine _engine = new();
    private readonly DispatcherTimer _meterTimer;
    private readonly DispatcherTimer _defaultDeviceTimer;
    private readonly DispatcherTimer _saveTimer;

    private float[] _inPeaks = new float[RoutingSnapshot.MaxInputs];
    private float[] _outPeaks = Array.Empty<float>();

    private OutputDeviceInfo? _selectedDevice;
    private string _selectedProcess;
    private string _selectedShareMode;
    private string _selectedLayoutOverride;
    private bool _isEnabled;
    private double _masterVolume;
    private SourceLayout _activeLayout = SourceLayout.Stereo;
    private string _defaultDeviceText = string.Empty;
    private string? _defaultDeviceId;
    private string _captureStatus = "Idle";
    private string _outputStatus = "Stopped";
    private string _warning = string.Empty;
    private double _toneHz;
    private double _toneLevel;
    private bool _tonePulsed;
    private TestSignalOption _testSignal = TestSignalOption.All[2];
    private string _sweepReadout = string.Empty;
    private int _testingChannel = -1;
    private bool _starting;
    private bool _initializing = true;
    private bool _refreshing;
    private string? _engineDeviceId;

    public MainViewModel(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _settings = _store.Load();

        _selectedProcess = string.IsNullOrWhiteSpace(_settings.TargetProcess) ? ProcessService.DefaultTarget : _settings.TargetProcess;
        _selectedShareMode = ShareModes.Contains(_settings.ShareMode) ? _settings.ShareMode : "Shared";
        _selectedLayoutOverride = LayoutOptions.Contains(_settings.LayoutOverride) ? _settings.LayoutOverride : "Auto";
        _toneHz = Math.Clamp(_settings.TestToneHz, 25, 80);
        _toneLevel = Math.Clamp(_settings.TestToneLevel, 5, 100);
        _tonePulsed = _settings.TestTonePulsed;
        _testSignal = TestSignalOption.Find(_settings.TestSignal);
        _isEnabled = _settings.Enabled;
        _masterVolume = Math.Round(Math.Clamp(_settings.MasterVolume, 0, 100));

        RefreshCommand = new RelayCommand(RefreshLists);
        CheckUpdatesCommand = new RelayCommand(() => _ = CheckForUpdatesAsync(userInitiated: true), () => !_updateBusy);
        InstallUpdateCommand = new RelayCommand(() => _ = InstallUpdateAsync(), () => _availableUpdate != null && !_updateBusy);
        ResetProfileCommand = new RelayCommand(ResetProfile);
        StopTestCommand = new RelayCommand(() => SetTestChannel(-1));

        _engine.CaptureStatusChanged += s => _dispatcher.InvokeAsync(() => CaptureStatus = s);
        _engine.OutputStopped += reason => _dispatcher.InvokeAsync(() =>
        {
            OutputStatus = $"Output stopped: {reason}";
            SaveNow();
            _engine.Stop();
            _engineDeviceId = null;
            ClearMatrix();
            Set(ref _isEnabled, false, nameof(IsEnabled));
        });

        _meterTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, (_, _) => UpdateMeters(), _dispatcher);
        _defaultDeviceTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => CheckDefaultDevice(), _dispatcher);
        _saveTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };

        RefreshLists();
        CheckDefaultDevice();

        _meterTimer.Start();
        _defaultDeviceTimer.Start();

        _initializing = false;

        UpdateService.CleanupOldFiles();
        _ = CheckForUpdatesAsync(userInitiated: false);
        if (_isEnabled) StartEngine();
    }

    // ------------------------------------------------------------------
    // Bindable state
    // ------------------------------------------------------------------

    public ObservableCollection<string> Processes { get; } = new();
    public ObservableCollection<OutputDeviceInfo> Devices { get; } = new();
    public string[] ShareModes { get; } = { "Shared", "Exclusive" };
    public string[] LayoutOptions { get; } = { "Auto", "Stereo", "5.1", "7.1" };

    public ObservableCollection<OutputChannelViewModel> Outputs { get; } = new();
    public ObservableCollection<InputRowViewModel> Inputs { get; } = new();

    public ICommand RefreshCommand { get; }
    public ICommand ResetProfileCommand { get; }
    public ICommand StopTestCommand { get; }
    public ICommand CheckUpdatesCommand { get; }
    public ICommand InstallUpdateCommand { get; }

    // ------------------------------------------------------------------
    // Updates (GitHub Releases)
    // ------------------------------------------------------------------

    private ReleaseInfo? _availableUpdate;
    private bool _updateBusy;
    private string _updateStatus = $"v{UpdateService.CurrentVersion}";

    public string UpdateStatus
    {
        get => _updateStatus;
        private set => Set(ref _updateStatus, value);
    }

    public bool UpdateAvailable => _availableUpdate != null;

    public string UpdateButtonText => _availableUpdate == null ? "Install update" : $"Install v{_availableUpdate.Version}";

    private async Task CheckForUpdatesAsync(bool userInitiated)
    {
        if (_updateBusy) return;
        _updateBusy = true;
        if (userInitiated) UpdateStatus = "Checking for updates...";
        try
        {
            if (!userInitiated) await Task.Delay(TimeSpan.FromSeconds(3)); // let startup finish first
            var release = await UpdateService.CheckAsync();
            _availableUpdate = release;
            UpdateStatus = release != null
                ? $"v{UpdateService.CurrentVersion}. Update available: v{release.Version}"
                : $"v{UpdateService.CurrentVersion}. Up to date";
        }
        catch (Exception ex)
        {
            UpdateStatus = userInitiated ? $"Update check failed: {ex.Message}" : $"v{UpdateService.CurrentVersion}";
        }
        finally
        {
            _updateBusy = false;
            OnPropertyChanged(nameof(UpdateAvailable));
            OnPropertyChanged(nameof(UpdateButtonText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task InstallUpdateAsync()
    {
        var release = _availableUpdate;
        if (release == null || _updateBusy) return;

        if (!UpdateService.CanWriteToAppFolder())
        {
            UpdateStatus = "Can't write to the app folder. Opening the download page.";
            UpdateService.OpenReleasesPage(release.PageUrl);
            return;
        }

        _updateBusy = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            // Save and let go of the shaker device before the new version starts.
            SetTestChannel(-1);
            SaveNow();
            bool wasRunning = _engine.IsRunning;
            _engine.Stop();
            _engineDeviceId = null;

            var progress = new Progress<double>(p => UpdateStatus = $"Downloading v{release.Version}... {p * 100:0}%");
            try
            {
                await UpdateService.InstallAsync(release, progress);
            }
            catch
            {
                if (wasRunning && _isEnabled) StartEngine(); // keep working on the current version
                throw;
            }

            UpdateStatus = "Restarting...";
            _skipSaveOnDispose = true; // already saved; the new version has loaded these settings
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            UpdateStatus = $"Update failed: {ex.Message}";
            _updateBusy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private bool _skipSaveOnDispose;

    public string SettingsPath => _store.FilePath;

    public string SelectedProcess
    {
        get => _selectedProcess;
        set
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length == 0) return;
            if (!Set(ref _selectedProcess, value)) return;
            _settings.TargetProcess = value;
            _engine.SetTargetProcess(value);
            ScheduleSave();
        }
    }

    public OutputDeviceInfo? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (value == null && (_refreshing || _initializing)) return; // ComboBox pushes null while its list is rebuilt
            if (!Set(ref _selectedDevice, value)) return;
            if (value != null) _settings.OutputDeviceId = value.Id;
            ScheduleSave();
            UpdateWarning();
            if (_isEnabled && !_initializing) StartEngine();
        }
    }

    public string SelectedShareMode
    {
        get => _selectedShareMode;
        set
        {
            if (value == null || !Set(ref _selectedShareMode, value)) return;
            _settings.ShareMode = value;
            ScheduleSave();
            if (_isEnabled && !_initializing) StartEngine();
        }
    }

    public string SelectedLayoutOverride
    {
        get => _selectedLayoutOverride;
        set
        {
            if (value == null || !Set(ref _selectedLayoutOverride, value)) return;
            _settings.LayoutOverride = value;
            ScheduleSave();
            ApplyLayout(ResolveLayout());
        }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (!Set(ref _isEnabled, value)) return;
            _settings.Enabled = value;
            ScheduleSave();
            if (value) StartEngine();
            else StopEngine();
        }
    }

    /// <summary>
    /// Master volume, 0 to 100 percent. Scales every output on top of its own gain and the
    /// channel levels, so turning it down keeps the balance you dialed in. Shared by all profiles.
    /// </summary>
    public double MasterVolume
    {
        get => _masterVolume;
        set
        {
            if (!Set(ref _masterVolume, Math.Round(Math.Clamp(value, 0, 100)))) return;
            _settings.MasterVolume = _masterVolume;
            _engine.Router?.SetMasterGain((float)(_masterVolume / 100.0));
            ScheduleSave();
        }
    }

    public string ActiveLayoutText => $"Routing profile: {SourceLayoutNames.Display(_activeLayout)}";

    public string DefaultDeviceText
    {
        get => _defaultDeviceText;
        private set => Set(ref _defaultDeviceText, value);
    }

    public string CaptureStatus
    {
        get => _captureStatus;
        private set => Set(ref _captureStatus, value);
    }

    public string OutputStatus
    {
        get => _outputStatus;
        private set => Set(ref _outputStatus, value);
    }

    public string Warning
    {
        get => _warning;
        private set
        {
            if (Set(ref _warning, value)) OnPropertyChanged(nameof(HasWarning));
        }
    }

    public bool HasWarning => _warning.Length > 0;

    public double ToneHz
    {
        get => _toneHz;
        set
        {
            if (!Set(ref _toneHz, Math.Round(Math.Clamp(value, 25, 80)))) return;
            _settings.TestToneHz = _toneHz;
            ScheduleSave();
            RestartToneIfActive();
        }
    }

    /// <summary>Test tone level, percent.</summary>
    public double ToneLevel
    {
        get => _toneLevel;
        set
        {
            if (!Set(ref _toneLevel, Math.Round(Math.Clamp(value, 5, 100)))) return;
            _settings.TestToneLevel = _toneLevel;
            ScheduleSave();
            RestartToneIfActive();
        }
    }

    public IReadOnlyList<TestSignalOption> TestSignals => TestSignalOption.All;

    /// <summary>What the Test button plays: a locator pulse or a game-like effect.</summary>
    public TestSignalOption SelectedTestSignal
    {
        get => _testSignal;
        set
        {
            if (value == null || !Set(ref _testSignal, value)) return;
            _settings.TestSignal = value.Signal.ToString();
            OnPropertyChanged(nameof(IsPulseSignal));
            OnPropertyChanged(nameof(TestSignalDescription));
            ScheduleSave();
            RestartToneIfActive();
        }
    }

    public bool IsPulseSignal => _testSignal.Signal == TestSignal.Pulse;
    public string TestSignalDescription => _testSignal.Description;

    /// <summary>Live frequency while the sweep plays, so you can note where a shaker peaks or goes dead.</summary>
    public string SweepReadout
    {
        get => _sweepReadout;
        private set => Set(ref _sweepReadout, value);
    }

    public bool TonePulsed
    {
        get => _tonePulsed;
        set
        {
            if (!Set(ref _tonePulsed, value)) return;
            _settings.TestTonePulsed = value;
            ScheduleSave();
            RestartToneIfActive();
        }
    }

    // ------------------------------------------------------------------
    // Engine lifecycle
    // ------------------------------------------------------------------

    private void StartEngine()
    {
        if (_starting) return;
        _starting = true;
        try
        {
            SaveNow();
            _testingChannel = -1;

            var device = _selectedDevice;
            if (device == null)
            {
                OutputStatus = "Pick an output device";
                return;
            }

            var mode = _selectedShareMode == "Exclusive" ? AudioClientShareMode.Exclusive : AudioClientShareMode.Shared;

            try
            {
                _engineDeviceId = null;
                _engine.Start(device.Id, mode, _selectedProcess);
                _engine.Router?.SetMasterGain((float)(_masterVolume / 100.0));
                _engineDeviceId = device.Id;
            }
            catch (Exception ex)
            {
                _engine.Stop();
                _engineDeviceId = null;
                ClearMatrix();
                OutputStatus = $"Could not open {device.Name}: {ex.Message}";
                return;
            }

            OutputStatus = $"Output: {device.Name} ({_engine.OutputFormatDescription})";
            BuildMatrix(device);
            ApplyLayout(ResolveLayout(), force: true);
        }
        finally
        {
            _starting = false;
        }
    }

    private void StopEngine()
    {
        SaveNow();
        _engine.Stop();
        _engineDeviceId = null;
        ClearMatrix();
        OutputStatus = "Stopped";
        CaptureStatus = "Idle";
    }

    private void BuildMatrix(OutputDeviceInfo device)
    {
        int outCount = _engine.Router?.OutputChannels ?? device.Channels;
        var names = ChannelLayout.OutputNames(device.ChannelMask, outCount);

        Outputs.Clear();
        Inputs.Clear();
        for (int o = 0; o < outCount; o++)
            Outputs.Add(new OutputChannelViewModel(this, o, names[o]));

        for (int i = 0; i < RoutingSnapshot.MaxInputs; i++)
        {
            var row = new InputRowViewModel(this, i, ChannelLayout.GameInputNames[i], InputDescriptions[i]);
            for (int o = 0; o < outCount; o++)
                row.Cells.Add(new MatrixCellViewModel(this, i, o));
            Inputs.Add(row);
        }

        _outPeaks = new float[outCount];
    }

    private void ClearMatrix()
    {
        Outputs.Clear();
        Inputs.Clear();
        _outPeaks = Array.Empty<float>();
        _testingChannel = -1;
    }

    // ------------------------------------------------------------------
    // Profiles (one per output device per source layout)
    // ------------------------------------------------------------------

    private SourceLayout ResolveLayout() => _selectedLayoutOverride switch
    {
        "Stereo" => SourceLayout.Stereo,
        "5.1" => SourceLayout.Surround51,
        "7.1" => SourceLayout.Surround71,
        _ => DeviceService.GetDefault()?.Layout ?? SourceLayout.Stereo,
    };

    private void ApplyLayout(SourceLayout layout, bool force = false)
    {
        if (!force && layout == _activeLayout) return;

        // Save the profile we're leaving before switching.
        if (!force) SaveNow();

        _activeLayout = layout;
        OnPropertyChanged(nameof(ActiveLayoutText));

        var router = _engine.Router;
        var deviceId = _engineDeviceId;
        if (router == null || deviceId == null) return;

        string key = SettingsStore.ProfileKey(deviceId, layout);
        RoutingSnapshot? snapshot = null;
        if (_settings.Profiles.TryGetValue(key, out var dto))
            snapshot = SettingsStore.FromDto(dto, router.OutputChannels);
        snapshot ??= DefaultProfiles.Create(layout, router.OutputChannels);

        router.Load(snapshot);
        SyncFromSnapshot(snapshot);
    }

    private void SyncFromSnapshot(RoutingSnapshot s)
    {
        for (int o = 0; o < Outputs.Count && o < s.OutputChannels; o++)
            Outputs[o].Sync(s.Gains[o] * 100.0, s.LowPassEnabled[o], s.Cutoffs[o], s.Muted[o],
                            s.HighPassEnabled[o], s.HighPassCutoffs[o], s.PresetIds[o]);

        foreach (var row in Inputs)
        {
            if (row.Index < s.InputGains.Length) row.Sync(s.InputGains[row.Index] * 100.0);
            foreach (var cell in row.Cells)
                cell.Sync(s.GetWeight(cell.InputIndex, cell.OutputIndex));
        }
    }

    private void ResetProfile()
    {
        var router = _engine.Router;
        if (router == null) return;
        var snapshot = DefaultProfiles.Create(_activeLayout, router.OutputChannels);
        router.Load(snapshot);
        SyncFromSnapshot(snapshot);
        ScheduleSave();
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveNow()
    {
        // Key by the device the router was built for, not the one just picked in the dropdown.
        var router = _engine.Router;
        var deviceId = _engineDeviceId;
        if (router != null && deviceId != null)
            _settings.Profiles[SettingsStore.ProfileKey(deviceId, _activeLayout)] = SettingsStore.ToDto(router.Current);
        _store.Save(_settings);
    }

    // ------------------------------------------------------------------
    // Edits from the matrix and channel strips
    // ------------------------------------------------------------------

    internal void OnRouteChanged(int input, int output, float weight)
    {
        _engine.Router?.SetRouteWeight(input, output, weight);
        ScheduleSave();
    }

    internal void OnInputGainChanged(int input, double percent)
    {
        _engine.Router?.SetInputGain(input, (float)(percent / 100.0));
        ScheduleSave();
    }

    internal void OnGainChanged(int output, double percent)
    {
        _engine.Router?.SetGain(output, (float)(percent / 100.0));
        ScheduleSave();
    }

    internal void OnBassOnlyChanged(int output, bool enabled)
    {
        _engine.Router?.SetLowPass(output, enabled);
        ScheduleSave();
    }

    internal void OnHighPassChanged(int output, bool enabled)
    {
        _engine.Router?.SetHighPass(output, enabled);
        ScheduleSave();
    }

    internal void OnHighPassCutoffChanged(int output, double hz)
    {
        _engine.Router?.SetHighPassCutoff(output, hz);
        ScheduleSave();
    }

    /// <summary>Preset picked (or reset): apply its filter baseline. Custom keeps the current filters.</summary>
    internal void OnPresetSelected(int output, TransducerPreset preset)
    {
        var router = _engine.Router;
        if (router == null) return;

        if (preset.Id == TransducerPresets.CustomId)
        {
            router.SetPresetId(output, TransducerPresets.CustomId);
        }
        else
        {
            router.ApplyPreset(output, preset.Id, preset.LowPass, preset.LowPassHz, preset.HighPass, preset.HighPassHz);
            if (output < Outputs.Count) Outputs[output].SyncFilters(preset);
        }
        ScheduleSave();
    }

    internal void OnCutoffChanged(int output, double hz)
    {
        _engine.Router?.SetCutoff(output, hz);
        ScheduleSave();
    }

    internal void OnMutedChanged(int output, bool muted)
    {
        _engine.Router?.SetMuted(output, muted);
        ScheduleSave();
    }

    // ------------------------------------------------------------------
    // Test tones
    // ------------------------------------------------------------------

    internal void ToggleTest(int output) => SetTestChannel(_testingChannel == output ? -1 : output);

    private void SetTestChannel(int output)
    {
        var router = _engine.Router;
        _testingChannel = router == null ? -1 : output;

        if (router != null)
        {
            if (_testingChannel >= 0) router.StartTestTone(_testingChannel, _toneHz, (float)(_toneLevel / 100.0), _tonePulsed, _testSignal.Signal);
            else router.StopTestTone();
        }

        foreach (var o in Outputs)
            o.IsTesting = o.Index == _testingChannel;
    }

    private void RestartToneIfActive()
    {
        if (_testingChannel >= 0) SetTestChannel(_testingChannel);
    }

    // ------------------------------------------------------------------
    // Polling
    // ------------------------------------------------------------------

    private void UpdateMeters()
    {
        var router = _engine.Router;
        if (router == null) return;

        if (_outPeaks.Length != router.OutputChannels) _outPeaks = new float[router.OutputChannels];
        router.ReadPeaks(_inPeaks, _outPeaks);

        for (int i = 0; i < Inputs.Count && i < _inPeaks.Length; i++)
            Inputs[i].Level = Ballistics(Inputs[i].Level, _inPeaks[i]);
        for (int o = 0; o < Outputs.Count && o < _outPeaks.Length; o++)
            Outputs[o].Level = Ballistics(Outputs[o].Level, _outPeaks[o]);

        double sweepHz = _testingChannel >= 0 && _testSignal.Signal == TestSignal.Sweep ? router.CurrentSweepHz : 0;
        SweepReadout = sweepHz > 0 ? $"Now: {sweepHz:0} Hz" : string.Empty;
    }

    /// <summary>Instant attack, smooth release, -48 dB floor.</summary>
    private static double Ballistics(double previous, float peak)
    {
        double norm = peak <= 1e-6f ? 0 : Math.Clamp((20.0 * Math.Log10(peak) + 48.0) / 48.0, 0, 1);
        return norm >= previous ? norm : previous * 0.85;
    }

    private void CheckDefaultDevice()
    {
        var def = DeviceService.GetDefault();
        if (def == null)
        {
            DefaultDeviceText = "Windows default: none";
            return;
        }

        bool changed = def.Id != _defaultDeviceId;
        _defaultDeviceId = def.Id;
        DefaultDeviceText = $"Windows default: {def.Name} ({SourceLayoutNames.Display(def.Layout)})";

        if (changed) UpdateWarning();
        if (_selectedLayoutOverride == "Auto") ApplyLayout(def.Layout);
    }

    private void UpdateWarning()
    {
        if (_selectedDevice != null && _selectedDevice.Id == _defaultDeviceId)
            Warning = "Your shaker device is the Windows default. The game would play through it twice. Pick your headset or speakers as the Windows default.";
        else
            Warning = string.Empty;
    }

    private void RefreshLists()
    {
        _refreshing = true;
        try { RefreshListsCore(); }
        finally { _refreshing = false; }
    }

    private void RefreshListsCore()
    {
        string currentProcess = _selectedProcess;
        Processes.Clear();
        foreach (var p in ProcessService.ListCandidates()) Processes.Add(p);
        if (!Processes.Contains(currentProcess, StringComparer.OrdinalIgnoreCase)) Processes.Insert(1, currentProcess);
        _selectedProcess = Processes.First(p => string.Equals(p, currentProcess, StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(SelectedProcess)); // re-push text: the editable ComboBox blanks it when the list is cleared

        string? wantedId = _selectedDevice?.Id ?? _settings.OutputDeviceId;
        Devices.Clear();
        foreach (var d in DeviceService.ListOutputs()) Devices.Add(d);

        var match = Devices.FirstOrDefault(d => d.Id == wantedId)
                    ?? Devices.FirstOrDefault(d => !d.IsDefault && d.Name.Contains("Sound Blaster", StringComparison.OrdinalIgnoreCase))
                    ?? Devices.FirstOrDefault(d => !d.IsDefault)
                    ?? Devices.FirstOrDefault();

        // Re-select without restarting the engine when it's the same device.
        if (match != null && _selectedDevice != null && match.Id == _selectedDevice.Id)
        {
            Set(ref _selectedDevice, match, nameof(SelectedDevice));
        }
        else
        {
            SelectedDevice = match;
        }

        UpdateWarning();
    }

    public void Dispose()
    {
        _meterTimer.Stop();
        _defaultDeviceTimer.Stop();
        _saveTimer.Stop();
        if (!_skipSaveOnDispose) SaveNow();
        _engine.Dispose();
    }
}

public sealed class TestSignalOption
{
    public required TestSignal Signal { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public override string ToString() => Name;

    public static readonly IReadOnlyList<TestSignalOption> All = new List<TestSignalOption>
    {
        new() { Signal = TestSignal.Pulse,   Name = "Pulse",           Description = "Steady sine on/off. Best for finding which jack is which." },
        new() { Signal = TestSignal.Engine,  Name = "Engine rumble",   Description = "Drifting 30-40 Hz drone over rumble. Like cruising with thrusters on." },
        new() { Signal = TestSignal.Gunfire, Name = "Gunfire burst",   Description = "Six punchy rounds, then a pause. Like ballistic guns." },
        new() { Signal = TestSignal.Impact,  Name = "Impact",          Description = "Heavy hit with a long rumble tail every 2.5 s. Like a crash or explosion." },
        new() { Signal = TestSignal.Sweep,   Name = "Sweep 20-120 Hz", Description = "Glides low to high. Note where a shaker peaks or goes quiet." },
    };

    public static TestSignalOption Find(string? name) =>
        All.FirstOrDefault(o => string.Equals(o.Signal.ToString(), name, System.StringComparison.OrdinalIgnoreCase)) ?? All[2];
}
