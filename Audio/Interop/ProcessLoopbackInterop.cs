using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace ShakerRouter.Audio.Interop;

// Minimal COM interop for Windows process loopback capture (Windows 10 build 20348+ / Windows 11).
// NAudio does not expose this activation path, so everything the capture needs is declared here.
// All methods use PreserveSig so HRESULTs come back as ints we can inspect.

[ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, IntPtr audioSessionGuid);
    [PreserveSig] int GetBufferSize(out uint numBufferFrames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint numPaddingFrames);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr pFormat, out IntPtr closestMatch);
    [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
    [PreserveSig] int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr eventHandle);
    [PreserveSig] int GetService([MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
}

[ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr data, out uint numFramesToRead, out uint flags, out ulong devicePosition, out ulong qpcPosition);
    [PreserveSig] int ReleaseBuffer(uint numFramesRead);
    [PreserveSig] int GetNextPacketSize(out uint numFramesInNextPacket);
}

[ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceAsyncOperation
{
    [PreserveSig] int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
}

[ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceCompletionHandler
{
    void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
}

/// <summary>Marker interface. Without it, activation fails because the callback cannot be marshaled.</summary>
[ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAgileObject
{
}

[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientActivationParams
{
    public int ActivationType;       // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK = 1
    public uint TargetProcessId;
    public int ProcessLoopbackMode;  // 0 = include target process tree, 1 = exclude
}

/// <summary>PROPVARIANT holding a VT_BLOB. Layout matches the native union on x86 and x64.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PropVariantBlob
{
    public ushort vt;
    public ushort reserved1;
    public ushort reserved2;
    public ushort reserved3;
    public uint cbSize;
    public IntPtr pBlobData;
}

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class ActivationCompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
{
    public readonly ManualResetEventSlim Completed = new(false);
    public IAudioClient? Client;
    public int HResult;

    public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
    {
        try
        {
            int hr = activateOperation.GetActivateResult(out int activateResult, out object activated);
            HResult = hr < 0 ? hr : activateResult;
            if (HResult >= 0)
                Client = (IAudioClient)activated;
        }
        catch (Exception ex)
        {
            HResult = ex.HResult;
        }
        finally
        {
            Completed.Set();
        }
    }
}

internal static class ProcessLoopbackInterop
{
    public const string VirtualDeviceProcessLoopback = "VAD\\Process_Loopback";
    public static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    public static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
    public static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    public const int AUDCLNT_SHAREMODE_SHARED = 0;
    public const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    public const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    public const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
    public const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
    public const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
    public const ushort VT_BLOB = 65;

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    /// <summary>
    /// Activates an IAudioClient that loops back audio rendered by one process (and its children).
    /// Call from an MTA thread.
    /// </summary>
    public static IAudioClient ActivateForProcess(int processId)
    {
        var activationParams = new AudioClientActivationParams
        {
            ActivationType = 1,
            TargetProcessId = (uint)processId,
            ProcessLoopbackMode = 0,
        };

        int paramsSize = Marshal.SizeOf<AudioClientActivationParams>();
        IntPtr pParams = Marshal.AllocHGlobal(paramsSize);
        IntPtr pVariant = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
        bool safeToFree = true;

        try
        {
            Marshal.StructureToPtr(activationParams, pParams, false);
            var variant = new PropVariantBlob { vt = VT_BLOB, cbSize = (uint)paramsSize, pBlobData = pParams };
            Marshal.StructureToPtr(variant, pVariant, false);

            var handler = new ActivationCompletionHandler();
            ActivateAudioInterfaceAsync(VirtualDeviceProcessLoopback, IID_IAudioClient, pVariant, handler, out var operation);

            bool completed = handler.Completed.Wait(TimeSpan.FromSeconds(5));
            GC.KeepAlive(operation);
            GC.KeepAlive(handler);

            if (!completed)
            {
                // Windows may still read the params after a timeout. Leak them instead of risking a crash.
                safeToFree = false;
                throw new TimeoutException("Process loopback activation timed out.");
            }

            if (handler.HResult < 0 || handler.Client == null)
                throw new COMException($"Process loopback activation failed (0x{handler.HResult:X8}). Requires Windows 10 build 20348 or Windows 11.", handler.HResult);

            return handler.Client;
        }
        finally
        {
            if (safeToFree)
            {
                Marshal.FreeHGlobal(pParams);
                Marshal.FreeHGlobal(pVariant);
            }
        }
    }

    /// <summary>Allocates a WAVEFORMATEXTENSIBLE (IEEE float) in unmanaged memory. Caller frees.</summary>
    public static IntPtr AllocFloatFormat(int sampleRate, int channels, uint channelMask)
    {
        const int size = 40;
        IntPtr p = Marshal.AllocHGlobal(size);
        short blockAlign = (short)(channels * 4);
        Marshal.WriteInt16(p, 0, unchecked((short)0xFFFE));          // wFormatTag = WAVE_FORMAT_EXTENSIBLE
        Marshal.WriteInt16(p, 2, (short)channels);                     // nChannels
        Marshal.WriteInt32(p, 4, sampleRate);                          // nSamplesPerSec
        Marshal.WriteInt32(p, 8, sampleRate * blockAlign);             // nAvgBytesPerSec
        Marshal.WriteInt16(p, 12, blockAlign);                         // nBlockAlign
        Marshal.WriteInt16(p, 14, 32);                                 // wBitsPerSample
        Marshal.WriteInt16(p, 16, 22);                                 // cbSize
        Marshal.WriteInt16(p, 18, 32);                                 // wValidBitsPerSample
        Marshal.WriteInt32(p, 20, unchecked((int)channelMask));        // dwChannelMask
        byte[] guid = SubtypeIeeeFloat.ToByteArray();
        Marshal.Copy(guid, 0, p + 24, 16);                             // SubFormat
        return p;
    }
}
