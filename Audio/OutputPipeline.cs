using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace EarShare.Audio;

/// <summary>
/// One fully independent playback chain per output device:
///
///   capture bytes -> BufferedWaveProvider -> (channel router) -> volume
///                 -> drift-correcting resampler -> WasapiOut (shared, event-driven)
///
/// Each device has its own buffer and its own WASAPI playback thread, so a slow
/// or stalling device can never block the capture thread or the other outputs —
/// the capture callback only copies bytes into each buffer and returns.
///
/// The buffer is held at (BaseTargetMs + extra delay) by the drift controller.
/// The optional per-device extra delay lets fast receivers (wired / 2.4 GHz)
/// be lined up with slow ones (classic Bluetooth like AirPods).
/// </summary>
public sealed class OutputPipeline : IDisposable
{
    // Steady-state queue per device: absorbs scheduling jitter between the capture
    // clock and each output's callback timing, and gives the drift controller room
    // to work. User-adjustable; lower = less latency, higher = more stutter-proof.
    // 20 ms is aggressive (capture arrives in 10 ms bursts, so fill swings ±10 ms);
    // expect occasional crackle under load there.
    public const int MinBufferMs = 20;
    public const int MaxBufferMs = 500;
    public const int MaxExtraDelayMs = 1000;

    private readonly MMDevice device;
    private readonly WaveFormat captureFormat;
    private readonly BufferedWaveProvider buffer;
    private readonly VolumeSampleProvider volume;
    private readonly DriftCorrectingResampler resampler;
    private readonly WasapiOut output;
    private volatile int baseTargetMs;
    private volatile int extraDelayMs;
    private volatile bool disposed;
    private long lastWriteTick;

    public string DeviceId { get; }
    public string FriendlyName { get; }
    public int DeviceSampleRate { get; }
    public double BufferedMs => buffer.BufferedDuration.TotalMilliseconds;

    /// <summary>Raised when playback stops without Dispose being called (device lost, BT dropout...).</summary>
    public event Action<OutputPipeline, Exception?>? Stopped;

    private double TargetFillMs => baseTargetMs + extraDelayMs;

    public OutputPipeline(MMDevice device, WaveFormat captureFormat, float volume01, int delayMs, int bufferMs)
    {
        this.device = device;
        this.captureFormat = captureFormat;
        baseTargetMs = Math.Clamp(bufferMs, MinBufferMs, MaxBufferMs);
        extraDelayMs = Math.Clamp(delayMs, 0, MaxExtraDelayMs);
        DeviceId = device.ID;
        FriendlyName = device.FriendlyName;

        // Output in the device's shared-mode mix format (always accepted by WASAPI
        // shared mode), resampling ourselves so we keep the drift-correction hook.
        var mixFormat = device.AudioClient.MixFormat;
        DeviceSampleRate = mixFormat.SampleRate;
        int deviceChannels = mixFormat.Channels;

        buffer = new BufferedWaveProvider(captureFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(3),
            DiscardOnBufferOverflow = true, // never throw on the capture thread
            ReadFully = true,               // pad with silence on underrun instead of stopping
        };

        ISampleProvider chain = buffer.ToSampleProvider();
        if (captureFormat.Channels != deviceChannels)
            chain = new ChannelRouterProvider(chain, deviceChannels);

        volume = new VolumeSampleProvider(chain) { Volume = volume01 };

        resampler = new DriftCorrectingResampler(volume, DeviceSampleRate,
            () => buffer.BufferedDuration.TotalMilliseconds, TargetFillMs);

        // Prime the queue with silence so the device starts at exactly its target
        // latency (base + extra delay) instead of converging there slowly.
        AddSilence(TargetFillMs);
        lastWriteTick = Environment.TickCount64;

        output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 20);
        output.Init(new SampleToWaveProvider(resampler));
        output.PlaybackStopped += (_, e) =>
        {
            if (!disposed)
                Stopped?.Invoke(this, e.Exception);
        };
        output.Play();
    }

    /// <summary>Called from the capture thread. Copies data and returns immediately.</summary>
    public void Write(byte[] data, int count)
    {
        if (disposed)
            return;
        try
        {
            long now = Environment.TickCount64;
            double target = TargetFillMs;
            if (buffer.BufferedDuration.TotalMilliseconds > target + 500)
            {
                // Device stalled long enough for audio to pile up: drop the backlog
                // and re-prime, so it rejoins the others instead of playing late.
                buffer.ClearBuffer();
                AddSilence(target);
            }
            else if (buffer.BufferedBytes == 0 && now - lastWriteTick > 250)
            {
                // Drained after a real capture gap (loopback delivers nothing while
                // the system is silent). Re-prime so playback resumes at the exact
                // target latency rather than crawling back to it. The gap check
                // matters at small buffer sizes, where a transient underrun from
                // scheduling jitter would otherwise trigger false re-primes.
                AddSilence(target);
            }
            buffer.AddSamples(data, 0, count);
            lastWriteTick = now;
        }
        catch
        {
            // benign race with Dispose
        }
    }

    public void SetVolume(float volume01) => volume.Volume = volume01;

    /// <summary>
    /// Change the extra delay while playing: takes effect immediately by inserting
    /// silence (more delay) or skipping queued audio (less delay), then the drift
    /// controller holds the new fill level.
    /// </summary>
    public void SetExtraDelay(int delayMs)
    {
        delayMs = Math.Clamp(delayMs, 0, MaxExtraDelayMs);
        ShiftTarget(delayMs - extraDelayMs);
        extraDelayMs = delayMs;
    }

    /// <summary>Change the base buffer size while playing, same instant-effect mechanism as the delay.</summary>
    public void SetBufferTarget(int bufferMs)
    {
        bufferMs = Math.Clamp(bufferMs, MinBufferMs, MaxBufferMs);
        ShiftTarget(bufferMs - baseTargetMs);
        baseTargetMs = bufferMs;
    }

    private void ShiftTarget(int deltaMs)
    {
        if (deltaMs == 0 || disposed)
            return;
        resampler.TargetBufferMs = TargetFillMs + deltaMs;
        if (deltaMs > 0)
            AddSilence(deltaMs);
        else
            Skip(-deltaMs);
    }

    private void AddSilence(double ms)
    {
        int bytes = MsToAlignedBytes(ms);
        if (bytes > 0)
            buffer.AddSamples(new byte[bytes], 0, bytes);
    }

    private void Skip(double ms)
    {
        int bytes = Math.Min(MsToAlignedBytes(ms), buffer.BufferedBytes);
        bytes -= bytes % captureFormat.BlockAlign;
        if (bytes > 0)
            buffer.Read(new byte[bytes], 0, bytes);
    }

    private int MsToAlignedBytes(double ms)
    {
        int bytes = (int)(captureFormat.AverageBytesPerSecond * ms / 1000.0);
        return bytes - bytes % captureFormat.BlockAlign;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        try { output.Stop(); } catch { }
        try { output.Dispose(); } catch { }
        try { device.Dispose(); } catch { }
    }
}
