using NAudio.Dsp;
using NAudio.Wave;

namespace EarShare.Audio;

/// <summary>
/// Resamples audio from the capture rate to the output device rate while
/// continuously nudging the conversion ratio (max ±0.3 %, inaudible) so that
/// the per-device input buffer stays near its target fill level.
///
/// This single mechanism absorbs both:
///  - nominal sample-rate differences (e.g. 44.1 kHz capture -> 48 kHz device), and
///  - physical clock drift between the capture device and each output device
///    (every Bluetooth headset runs on its own crystal and drifts slightly).
///
/// It is therefore used even when capture and device rates are nominally equal.
/// </summary>
public sealed class DriftCorrectingResampler : ISampleProvider
{
    // ±0.3 % max speed trim: corrects ~180 ms of drift per minute, far more than
    // real-world clock error (~±0.01 %), yet well below the audibility threshold.
    private const double MaxTrim = 0.003;
    private const double GainPerMs = 0.00004;   // 50 ms of buffer error -> 0.2 % trim
    private const double Smoothing = 0.15;      // low-pass on trim changes to avoid pitch wobble
    private const int AdjustIntervalMs = 100;

    private readonly ISampleProvider source;
    private readonly WdlResampler resampler = new();
    private readonly int channels;
    private readonly double sourceRate;
    private readonly double targetRate;
    private readonly Func<double> bufferedMs;

    private double trim;
    private long nextAdjustAt;

    public WaveFormat WaveFormat { get; }

    /// <summary>Fill level (ms) the feedback loop steers towards. Adjustable at runtime (per-device delay).</summary>
    public double TargetBufferMs { get; set; }

    /// <param name="bufferedMs">Returns the current fill level (ms) of the buffer feeding this chain.</param>
    /// <param name="targetBufferMs">Fill level the feedback loop steers towards.</param>
    public DriftCorrectingResampler(ISampleProvider source, int targetSampleRate,
        Func<double> bufferedMs, double targetBufferMs)
    {
        this.source = source;
        this.bufferedMs = bufferedMs;
        TargetBufferMs = targetBufferMs;
        channels = source.WaveFormat.Channels;
        sourceRate = source.WaveFormat.SampleRate;
        targetRate = targetSampleRate;

        resampler.SetMode(true, 2, false);
        resampler.SetFilterParms();
        resampler.SetFeedMode(false); // output driven (WasapiOut pulls)
        resampler.SetRates(sourceRate, targetRate);

        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(targetSampleRate, channels);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        long now = Environment.TickCount64;
        if (now >= nextAdjustAt)
        {
            nextAdjustAt = now + AdjustIntervalMs;
            Adjust();
        }

        int framesRequested = count / channels;
        int framesNeeded = resampler.ResamplePrepare(framesRequested, channels, out float[] inBuffer, out int inBufferOffset);
        int framesAvailable = source.Read(inBuffer, inBufferOffset, framesNeeded * channels) / channels;
        int framesOut = resampler.ResampleOut(buffer, offset, framesAvailable, framesRequested, channels);
        return framesOut * channels;
    }

    private void Adjust()
    {
        // error > 0: buffer filling up (device clock slower than capture) -> consume input faster.
        // error < 0: buffer draining (device clock faster) -> consume input slower.
        double error = bufferedMs() - TargetBufferMs;
        double desired = Math.Clamp(error * GainPerMs, -MaxTrim, MaxTrim);
        trim += Smoothing * (desired - trim);
        resampler.SetRates(sourceRate * (1.0 + trim), targetRate);
    }
}
