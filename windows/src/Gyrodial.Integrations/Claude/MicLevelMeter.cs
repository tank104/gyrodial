using System.Runtime.InteropServices;

namespace Gyrodial.Integrations.Claude;

/// <summary>
/// How loud the default microphone is, 0..1, for the dial's listening ring. Opens the mic in
/// shared mode next to the recognizer (winmm waveIn: 16 kHz mono, 50 ms buffers).
/// </summary>
sealed class MicLevelMeter : IDisposable
{
    const int SampleRate = 16000;
    const int BufferMs = 50;
    const int BufferBytes = SampleRate * 2 * BufferMs / 1000;
    const int BufferCount = 3;

    readonly Thread _thread;
    readonly AutoResetEvent _event = new(false);
    volatile bool _stop;
    float _level;
    // Who disposes _event: winmm signals it until waveInClose, so it may only go once the capture
    // thread is done with the device. 0 = running, 1 = Dispose gave up waiting (the thread disposes
    // it on exit), 2 = the thread has exited.
    int _eventOwner;

    /// <summary>The latest peak, with a quick attack and a slower fall so the ring breathes.</summary>
    public float Level => Volatile.Read(ref _level);

    MicLevelMeter()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Gyrodial mic level" };
    }

    /// <summary>Starts metering. Without a microphone the level just stays 0 (the dial's ring then pulses on its own).</summary>
    public static MicLevelMeter TryStart()
    {
        var meter = new MicLevelMeter();
        meter._thread.Start();
        return meter;
    }

    void Run()
    {
        try
        {
            Capture();
        }
        finally
        {
            if (Interlocked.Exchange(ref _eventOwner, 2) == 1) _event.Dispose();
        }
    }

    void Capture()
    {
        var format = new WaveFormatEx
        {
            wFormatTag = 1,  // PCM
            nChannels = 1,
            nSamplesPerSec = SampleRate,
            wBitsPerSample = 16,
            nBlockAlign = 2,
            nAvgBytesPerSec = SampleRate * 2,
        };
        if (waveInOpen(out var handle, WaveMapper, ref format, _event.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CallbackEvent) != 0)
            return;

        var headerSize = Marshal.SizeOf<WaveHdr>();
        var headers = new IntPtr[BufferCount];
        var buffers = new IntPtr[BufferCount];
        try
        {
            for (var i = 0; i < BufferCount; i++)
            {
                buffers[i] = Marshal.AllocHGlobal(BufferBytes);
                headers[i] = Marshal.AllocHGlobal(headerSize);
                Marshal.StructureToPtr(new WaveHdr { lpData = buffers[i], dwBufferLength = BufferBytes }, headers[i], false);
                waveInPrepareHeader(handle, headers[i], headerSize);
                waveInAddBuffer(handle, headers[i], headerSize);
            }
            waveInStart(handle);

            var samples = new short[BufferBytes / 2];
            while (!_stop)
            {
                _event.WaitOne(200);
                for (var i = 0; i < BufferCount && !_stop; i++)
                {
                    var hdr = Marshal.PtrToStructure<WaveHdr>(headers[i]);
                    if ((hdr.dwFlags & WhdrDone) == 0) continue;
                    var count = (int)Math.Min(hdr.dwBytesRecorded / 2, (uint)samples.Length);
                    Marshal.Copy(buffers[i], samples, 0, count);
                    var peak = 0;
                    for (var s = 0; s < count; s++) peak = Math.Max(peak, Math.Abs((int)samples[s]));
                    // speech sits well below full scale: map roughly -40..-6 dBFS onto 0..1
                    var db = 20 * Math.Log10(Math.Max(peak, 1) / 32768.0);
                    var target = (float)Math.Clamp((db + 40) / 34, 0, 1);
                    var current = Level;
                    Volatile.Write(ref _level, target > current ? target : current * 0.8f + target * 0.2f);
                    waveInUnprepareHeader(handle, headers[i], headerSize);
                    Marshal.StructureToPtr(new WaveHdr { lpData = buffers[i], dwBufferLength = BufferBytes }, headers[i], false);
                    waveInPrepareHeader(handle, headers[i], headerSize);
                    waveInAddBuffer(handle, headers[i], headerSize);
                }
            }
            waveInReset(handle);
            for (var i = 0; i < BufferCount; i++) waveInUnprepareHeader(handle, headers[i], headerSize);
        }
        finally
        {
            waveInClose(handle);
            for (var i = 0; i < BufferCount; i++)
            {
                if (headers[i] != IntPtr.Zero) Marshal.FreeHGlobal(headers[i]);
                if (buffers[i] != IntPtr.Zero) Marshal.FreeHGlobal(buffers[i]);
            }
        }
    }

    public void Dispose()
    {
        _stop = true;
        _event.Set();
        // A capture thread that's still inside winmm keeps the event: it disposes it when it exits
        if (_thread.Join(1000) || Interlocked.Exchange(ref _eventOwner, 1) == 2) _event.Dispose();
    }

    // ---- winmm

    const uint WaveMapper = unchecked((uint)-1);
    const uint CallbackEvent = 0x00050000;
    const uint WhdrDone = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    struct WaveFormatEx
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WaveHdr
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [DllImport("winmm.dll")]
    static extern int waveInOpen(out IntPtr hWaveIn, uint uDeviceID, ref WaveFormatEx lpFormat, IntPtr dwCallback, IntPtr dwInstance, uint dwFlags);
    [DllImport("winmm.dll")]
    static extern int waveInPrepareHeader(IntPtr hWaveIn, IntPtr lpWaveInHdr, int uSize);
    [DllImport("winmm.dll")]
    static extern int waveInUnprepareHeader(IntPtr hWaveIn, IntPtr lpWaveInHdr, int uSize);
    [DllImport("winmm.dll")]
    static extern int waveInAddBuffer(IntPtr hWaveIn, IntPtr lpWaveInHdr, int uSize);
    [DllImport("winmm.dll")]
    static extern int waveInStart(IntPtr hWaveIn);
    [DllImport("winmm.dll")]
    static extern int waveInReset(IntPtr hWaveIn);
    [DllImport("winmm.dll")]
    static extern int waveInClose(IntPtr hWaveIn);
}
