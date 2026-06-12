using NAudio.Wave;

namespace EarShare.Audio;

/// <summary>
/// Maps any input channel count to any output channel count so a stereo capture
/// can feed a mono headset, a quad device, etc.
/// Mono in -> copied to all outputs; mono out -> average of all inputs;
/// otherwise output channel c takes input channel (c mod inputChannels)
/// (system loopback capture is stereo in practice, so this covers the real cases).
/// </summary>
public sealed class ChannelRouterProvider : ISampleProvider
{
    private readonly ISampleProvider source;
    private readonly int inChannels;
    private readonly int outChannels;
    private float[] inBuffer = Array.Empty<float>();

    public WaveFormat WaveFormat { get; }

    public ChannelRouterProvider(ISampleProvider source, int outChannels)
    {
        this.source = source;
        this.outChannels = outChannels;
        inChannels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, outChannels);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / outChannels;
        int samplesNeeded = frames * inChannels;
        if (inBuffer.Length < samplesNeeded)
            inBuffer = new float[samplesNeeded];

        int framesRead = source.Read(inBuffer, 0, samplesNeeded) / inChannels;

        for (int f = 0; f < framesRead; f++)
        {
            int i = f * inChannels;
            int o = offset + f * outChannels;

            if (inChannels == 1)
            {
                for (int c = 0; c < outChannels; c++)
                    buffer[o + c] = inBuffer[i];
            }
            else if (outChannels == 1)
            {
                float sum = 0f;
                for (int c = 0; c < inChannels; c++)
                    sum += inBuffer[i + c];
                buffer[o] = sum / inChannels;
            }
            else
            {
                for (int c = 0; c < outChannels; c++)
                    buffer[o + c] = inBuffer[i + (c % inChannels)];
            }
        }

        return framesRead * outChannels;
    }
}
