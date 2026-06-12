using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace EarShare.Audio;

public sealed record OutputRequest(string DeviceId, float Volume01, int DelayMs = 0);

/// <summary>Result of trying to attach one output device.</summary>
public sealed record OutputStatus(string DeviceId, string Detail, bool Ok);

/// <summary>
/// Captures the Windows default playback device via WASAPI loopback and fans the
/// stream out to any number of <see cref="OutputPipeline"/>s. Outputs can be
/// added, removed and re-volumed while running.
/// </summary>
public sealed class MirrorEngine : IDisposable
{
    private readonly object sync = new();
    private MMDevice? captureDevice;
    private WasapiLoopbackCapture? capture;
    private volatile OutputPipeline[] pipelines = Array.Empty<OutputPipeline>();
    private volatile bool stopping;

    public bool IsRunning { get; private set; }
    public string? CaptureDeviceId { get; private set; }
    public string? CaptureDeviceName { get; private set; }
    public WaveFormat? CaptureFormat { get; private set; }
    public int BufferTargetMs { get; private set; } = 40;

    /// <summary>An output died while mirroring (e.g. Bluetooth headset disconnected). May fire on any thread.</summary>
    public event Action<OutputPipeline, Exception?>? OutputFailed;

    /// <summary>Capture stopped unexpectedly (default device changed/removed). May fire on any thread.</summary>
    public event Action<Exception?>? CaptureStopped;

    /// <param name="captureDeviceId">Endpoint to capture; null/missing/inactive falls back to the Windows default output.</param>
    /// <param name="bufferMs">Per-device buffer target (added latency vs. stutter robustness).</param>
    public List<OutputStatus> Start(IReadOnlyList<OutputRequest> requests, string? captureDeviceId = null, int bufferMs = 40)
    {
        lock (sync)
        {
            var statuses = new List<OutputStatus>();
            if (IsRunning)
                return statuses;

            BufferTargetMs = Math.Clamp(bufferMs, OutputPipeline.MinBufferMs, OutputPipeline.MaxBufferMs);
            using var enumerator = new MMDeviceEnumerator();
            captureDevice = ResolveCaptureDevice(enumerator, captureDeviceId);
            CaptureDeviceId = captureDevice.ID;
            CaptureDeviceName = captureDevice.FriendlyName;
            capture = new WasapiLoopbackCapture(captureDevice);
            CaptureFormat = capture.WaveFormat;

            var started = new List<OutputPipeline>();
            foreach (var request in requests)
                statuses.Add(TryCreatePipeline(enumerator, request, started));

            if (started.Count == 0)
            {
                CleanupCapture();
                return statuses;
            }

            pipelines = started.ToArray();
            stopping = false;
            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;
            capture.StartRecording();
            IsRunning = true;
            return statuses;
        }
    }

    public void Stop()
    {
        lock (sync)
        {
            if (!IsRunning)
                return;
            stopping = true;
            IsRunning = false;

            if (capture != null)
            {
                capture.DataAvailable -= OnDataAvailable;
                capture.RecordingStopped -= OnRecordingStopped;
                try { capture.StopRecording(); } catch { }
            }
            CleanupCapture();

            var snapshot = pipelines;
            pipelines = Array.Empty<OutputPipeline>();
            foreach (var pipeline in snapshot)
                pipeline.Dispose();
        }
    }

    public OutputStatus AddOutput(OutputRequest request)
    {
        lock (sync)
        {
            if (!IsRunning || capture == null)
                return new OutputStatus(request.DeviceId, "not running", false);
            if (pipelines.Any(p => p.DeviceId == request.DeviceId))
                return new OutputStatus(request.DeviceId, "already active", true);

            using var enumerator = new MMDeviceEnumerator();
            var started = new List<OutputPipeline>();
            var status = TryCreatePipeline(enumerator, request, started);
            if (started.Count == 1)
            {
                var list = pipelines.ToList();
                list.Add(started[0]);
                pipelines = list.ToArray();
            }
            return status;
        }
    }

    public void RemoveOutput(string deviceId)
    {
        OutputPipeline? removed = null;
        lock (sync)
        {
            var list = pipelines.ToList();
            removed = list.FirstOrDefault(p => p.DeviceId == deviceId);
            if (removed != null)
            {
                list.Remove(removed);
                pipelines = list.ToArray();
            }
        }
        removed?.Dispose();
    }

    public void SetVolume(string deviceId, float volume01)
    {
        var snapshot = pipelines;
        foreach (var pipeline in snapshot)
            if (pipeline.DeviceId == deviceId)
                pipeline.SetVolume(volume01);
    }

    public void SetOutputDelay(string deviceId, int delayMs)
    {
        var snapshot = pipelines;
        foreach (var pipeline in snapshot)
            if (pipeline.DeviceId == deviceId)
                pipeline.SetExtraDelay(delayMs);
    }

    /// <summary>Apply a new buffer target to all current outputs (takes effect immediately).</summary>
    public void SetBufferTarget(int bufferMs)
    {
        BufferTargetMs = Math.Clamp(bufferMs, OutputPipeline.MinBufferMs, OutputPipeline.MaxBufferMs);
        var snapshot = pipelines;
        foreach (var pipeline in snapshot)
            pipeline.SetBufferTarget(BufferTargetMs);
    }

    public bool TryGetOutputInfo(string deviceId, out int sampleRate, out double bufferedMs)
    {
        var snapshot = pipelines;
        foreach (var pipeline in snapshot)
        {
            if (pipeline.DeviceId == deviceId)
            {
                sampleRate = pipeline.DeviceSampleRate;
                bufferedMs = pipeline.BufferedMs;
                return true;
            }
        }
        sampleRate = 0;
        bufferedMs = 0;
        return false;
    }

    public void Dispose() => Stop();

    public static List<(string Id, string Name)> ListRenderDevices()
    {
        var result = new List<(string, string)>();
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            using (device)
                result.Add((device.ID, device.FriendlyName));
        return result;
    }

    public static string? GetDefaultRenderId()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return device.ID;
        }
        catch
        {
            return null;
        }
    }

    private static MMDevice ResolveCaptureDevice(MMDeviceEnumerator enumerator, string? requestedId)
    {
        if (!string.IsNullOrEmpty(requestedId))
        {
            try
            {
                var device = enumerator.GetDevice(requestedId);
                if (device.State == DeviceState.Active)
                    return device;
                device.Dispose();
            }
            catch
            {
                // fall through to default
            }
        }
        return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    private OutputStatus TryCreatePipeline(MMDeviceEnumerator enumerator, OutputRequest request, List<OutputPipeline> started)
    {
        // Outputting to the capture device would loop our own audio back into the
        // capture (echo build-up), so it is always refused.
        if (request.DeviceId == CaptureDeviceId)
            return new OutputStatus(request.DeviceId, "skipped — capture source", false);

        try
        {
            var device = enumerator.GetDevice(request.DeviceId);
            if (device.State != DeviceState.Active)
            {
                device.Dispose();
                return new OutputStatus(request.DeviceId, "not connected", false);
            }
            var pipeline = new OutputPipeline(device, capture!.WaveFormat, request.Volume01, request.DelayMs, BufferTargetMs);
            pipeline.Stopped += OnPipelineStopped;
            started.Add(pipeline);
            return new OutputStatus(request.DeviceId, $"{pipeline.DeviceSampleRate / 1000.0:0.#} kHz", true);
        }
        catch (Exception ex)
        {
            return new OutputStatus(request.DeviceId, "failed: " + ex.Message, false);
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        // Hot path: one copy into each device's own buffer, nothing that can block.
        var snapshot = pipelines;
        for (int i = 0; i < snapshot.Length; i++)
            snapshot[i].Write(e.Buffer, e.BytesRecorded);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (!stopping)
            CaptureStopped?.Invoke(e.Exception);
    }

    private void OnPipelineStopped(OutputPipeline pipeline, Exception? exception)
    {
        bool removed = false;
        lock (sync)
        {
            var list = pipelines.ToList();
            removed = list.Remove(pipeline);
            if (removed)
                pipelines = list.ToArray();
        }
        pipeline.Dispose();
        if (removed && !stopping)
            OutputFailed?.Invoke(pipeline, exception);
    }

    private void CleanupCapture()
    {
        try { capture?.Dispose(); } catch { }
        capture = null;
        try { captureDevice?.Dispose(); } catch { }
        captureDevice = null;
        CaptureFormat = null;
        CaptureDeviceId = null;
        CaptureDeviceName = null;
    }
}
