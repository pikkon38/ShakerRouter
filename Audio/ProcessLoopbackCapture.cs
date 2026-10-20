using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.Wave;
using ShakerRouter.Audio.Interop;

namespace ShakerRouter.Audio;

/// <summary>
/// Captures the audio one process renders (Star Citizen) without touching the default device.
///
/// Runs on its own MTA thread:
///   1. Wait for the target process by name.
///   2. Activate a process loopback IAudioClient for its PID.
///   3. Request 7.1 float at the OUTPUT device sample rate (Windows converts for us),
///      falling back to 5.1 and stereo if the request is refused.
///   4. Normalize every packet into 8 fixed slots (FL FR C LFE RL RR SL SR) so the
///      routing matrix rows always mean the same speaker.
///   5. Reattach automatically when the game restarts.
///
/// Output goes into a BufferedWaveProvider (8ch float). The capture thread also trims
/// that buffer when it grows, which absorbs clock drift between the game's device and the X4.
/// </summary>
public sealed class ProcessLoopbackCapture : IDisposable
{
    // Matrix row order, expressed as WAVEFORMATEXTENSIBLE speaker bits.
    private static readonly uint[] SlotBits = { 0x1, 0x2, 0x4, 0x8, 0x10, 0x20, 0x200, 0x400 };
    public const int Slots = 8;

    // Formats to try, best first.
    private static readonly (int Channels, uint Mask, string Name)[] FormatLadder =
    {
        (8, 0x63F, "7.1"),
        (6, 0x60F, "5.1"),
        (6, 0x03F, "5.1 (back)"),
        (2, 0x003, "Stereo"),
    };

    private readonly BufferedWaveProvider _target;
    private readonly int _sampleRate;
    private readonly int _targetBytes;
    private readonly int _maxBytes;
    private readonly ManualResetEvent _stop = new(false);
    private Thread? _thread;
    private volatile string _processName;

    private float[] _captured = Array.Empty<float>();
    private float[] _slots = Array.Empty<float>();
    private byte[] _bytes = Array.Empty<byte>();
    private byte[] _discard = new byte[64 * 1024];

    /// <summary>Raised from the capture thread. Marshal to the UI yourself.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Speaker layout the capture session negotiated, e.g. "7.1". Empty while not capturing.</summary>
    public string NegotiatedLayout { get; private set; } = string.Empty;

    public ProcessLoopbackCapture(BufferedWaveProvider target, string processName)
    {
        if (target.WaveFormat.Channels != Slots)
            throw new ArgumentException("Capture target must be an 8-channel float buffer.");

        _target = target;
        _sampleRate = target.WaveFormat.SampleRate;
        _processName = Normalize(processName);

        int bytesPerSecond = target.WaveFormat.AverageBytesPerSecond;
        int blockAlign = target.WaveFormat.BlockAlign;
        _targetBytes = AlignDown(bytesPerSecond * 40 / 1000, blockAlign);  // aim for ~40 ms buffered
        _maxBytes = AlignDown(bytesPerSecond * 120 / 1000, blockAlign);    // trim when above ~120 ms
    }

    public string ProcessName
    {
        get => _processName;
        set => _processName = Normalize(value);
    }

    public void Start()
    {
        if (_thread != null) return;
        _stop.Reset();
        _thread = new Thread(Run) { IsBackground = true, Name = "ShakerRouter Capture", Priority = ThreadPriority.AboveNormal };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public void Stop()
    {
        _stop.Set();
        _thread?.Join(2000);
        _thread = null;
    }

    public void Dispose()
    {
        Stop();
        _stop.Dispose();
    }

    private void Run()
    {
        string lastStatus = string.Empty;
        void Report(string s)
        {
            if (s == lastStatus) return;
            lastStatus = s;
            StatusChanged?.Invoke(s);
        }

        while (!_stop.WaitOne(0))
        {
            string name = _processName;
            int pid = FindNewestProcess(name);
            if (pid <= 0)
            {
                NegotiatedLayout = string.Empty;
                Report($"Waiting for {name}.exe");
                _stop.WaitOne(1500);
                continue;
            }

            try
            {
                RunSession(pid, name, Report);
            }
            catch (Exception ex)
            {
                NegotiatedLayout = string.Empty;
                Report($"Capture error: {ex.Message}");
                _stop.WaitOne(3000);
            }
        }

        NegotiatedLayout = string.Empty;
        Report("Capture stopped");
    }

    private void RunSession(int pid, string name, Action<string> report)
    {
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        using var audioEvent = new AutoResetEvent(false);

        try
        {
            client = ProcessLoopbackInterop.ActivateForProcess(pid);

            int channels = 0;
            uint mask = 0;
            string layoutName = string.Empty;
            int lastHr = 0;

            const uint flags = ProcessLoopbackInterop.AUDCLNT_STREAMFLAGS_LOOPBACK
                             | ProcessLoopbackInterop.AUDCLNT_STREAMFLAGS_EVENTCALLBACK
                             | ProcessLoopbackInterop.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM
                             | ProcessLoopbackInterop.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;

            foreach (var candidate in FormatLadder)
            {
                IntPtr fmt = ProcessLoopbackInterop.AllocFloatFormat(_sampleRate, candidate.Channels, candidate.Mask);
                try
                {
                    lastHr = client.Initialize(ProcessLoopbackInterop.AUDCLNT_SHAREMODE_SHARED, flags,
                        1_000_000 /* 100 ms */, 0, fmt, IntPtr.Zero);
                }
                finally
                {
                    Marshal.FreeHGlobal(fmt);
                }

                if (lastHr >= 0)
                {
                    channels = candidate.Channels;
                    mask = candidate.Mask;
                    layoutName = candidate.Name;
                    break;
                }

                // A failed Initialize can leave the client unusable. Re-activate before the next attempt.
                Marshal.ReleaseComObject(client);
                client = ProcessLoopbackInterop.ActivateForProcess(pid);
            }

            if (channels == 0)
                throw new COMException($"No capture format accepted (0x{lastHr:X8}).", lastHr);

            int[] slotForChannel = BuildSlotMap(mask, channels);

            Check(client.GetService(ProcessLoopbackInterop.IID_IAudioCaptureClient, out object captureObj), "GetService");
            capture = (IAudioCaptureClient)captureObj;

            Check(client.SetEventHandle(audioEvent.SafeWaitHandle.DangerousGetHandle()), "SetEventHandle");
            Check(client.Start(), "Start");

            NegotiatedLayout = layoutName;
            report($"Capturing {name}.exe (PID {pid}) as {layoutName}");

            var waitHandles = new WaitHandle[] { audioEvent, _stop };
            var aliveCheck = Stopwatch.StartNew();

            while (true)
            {
                int signaled = WaitHandle.WaitAny(waitHandles, 200);
                if (signaled == 1) break; // stop requested

                Drain(capture, channels, slotForChannel);

                if (aliveCheck.ElapsedMilliseconds > 1000)
                {
                    aliveCheck.Restart();
                    if (!string.Equals(_processName, name, StringComparison.OrdinalIgnoreCase)) break; // target changed
                    if (!IsRunning(name, pid))
                    {
                        report($"{name}.exe exited. Waiting for it to restart");
                        break;
                    }
                }
            }

            client.Stop();
        }
        finally
        {
            NegotiatedLayout = string.Empty;
            if (capture != null) Marshal.ReleaseComObject(capture);
            if (client != null) Marshal.ReleaseComObject(client);
        }
    }

    private void Drain(IAudioCaptureClient capture, int channels, int[] slotForChannel)
    {
        while (true)
        {
            Check(capture.GetNextPacketSize(out uint packetFrames), "GetNextPacketSize");
            if (packetFrames == 0) return;

            int hr = capture.GetBuffer(out IntPtr data, out uint frames, out uint bufferFlags, out _, out _);
            if (hr < 0) Check(hr, "GetBuffer");
            if (frames == 0)
            {
                capture.ReleaseBuffer(0);
                return;
            }

            int frameCount = (int)frames;
            int capturedSamples = frameCount * channels;
            int slotSamples = frameCount * Slots;
            if (_captured.Length < capturedSamples) _captured = new float[capturedSamples];
            if (_slots.Length < slotSamples) _slots = new float[slotSamples];
            if (_bytes.Length < slotSamples * 4) _bytes = new byte[slotSamples * 4];

            bool silent = (bufferFlags & ProcessLoopbackInterop.AUDCLNT_BUFFERFLAGS_SILENT) != 0;
            if (silent) Array.Clear(_captured, 0, capturedSamples);
            else Marshal.Copy(data, _captured, 0, capturedSamples);

            capture.ReleaseBuffer(frames);

            // Spread captured channels into the 8 fixed slots.
            Array.Clear(_slots, 0, slotSamples);
            for (int f = 0; f < frameCount; f++)
            {
                int src = f * channels;
                int dst = f * Slots;
                for (int c = 0; c < channels; c++)
                {
                    int slot = slotForChannel[c];
                    if (slot >= 0) _slots[dst + slot] = _captured[src + c];
                }
            }

            int byteCount = slotSamples * 4;
            Buffer.BlockCopy(_slots, 0, _bytes, 0, byteCount);
            _target.AddSamples(_bytes, 0, byteCount);
            TrimDrift();
        }
    }

    /// <summary>
    /// The game's device and the X4 run on separate clocks. If capture runs faster, the buffer
    /// grows and latency creeps up. Drop the excess in one go when it passes the ceiling.
    /// A few ms of dropped audio is not perceptible through a transducer.
    /// </summary>
    private void TrimDrift()
    {
        int buffered = _target.BufferedBytes;
        if (buffered <= _maxBytes) return;

        int excess = AlignDown(buffered - _targetBytes, _target.WaveFormat.BlockAlign);
        while (excess > 0)
        {
            int chunk = Math.Min(excess, _discard.Length);
            chunk = AlignDown(chunk, _target.WaveFormat.BlockAlign);
            if (chunk <= 0) break;
            _target.Read(_discard, 0, chunk);
            excess -= chunk;
        }
    }

    private static int[] BuildSlotMap(uint mask, int channels)
    {
        var map = new int[channels];
        int c = 0;
        for (int bit = 0; bit < 32 && c < channels; bit++)
        {
            uint flag = 1u << bit;
            if ((mask & flag) == 0) continue;
            map[c++] = Array.IndexOf(SlotBits, flag);
        }
        for (; c < channels; c++) map[c] = -1;
        return map;
    }

    private static int FindNewestProcess(string name)
    {
        Process[] procs;
        try { procs = Process.GetProcessesByName(name); }
        catch { return -1; }

        int pid = -1;
        DateTime newest = DateTime.MinValue;
        foreach (var p in procs)
        {
            try
            {
                DateTime started;
                try { started = p.StartTime; }
                catch { started = DateTime.MinValue; } // protected processes may deny this
                if (pid < 0 || started > newest)
                {
                    newest = started;
                    pid = p.Id;
                }
            }
            finally
            {
                p.Dispose();
            }
        }
        return pid;
    }

    private static bool IsRunning(string name, int pid)
    {
        try
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                bool match = p.Id == pid;
                p.Dispose();
                if (match) return true;
            }
        }
        catch
        {
            return true; // if we can't tell, keep capturing
        }
        return false;
    }

    private static string Normalize(string name)
    {
        name = (name ?? string.Empty).Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return name;
    }

    private static int AlignDown(int value, int align) => value - (value % align);

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new COMException($"{what} failed (0x{hr:X8})", hr);
    }
}
