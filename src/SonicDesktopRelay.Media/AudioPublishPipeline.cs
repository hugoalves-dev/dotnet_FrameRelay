namespace SonicDesktopRelay.Media;

/// <summary>
/// capture -> encode -> one encoded audio event for the whole publishing session.
/// The encoded sample is fanned out by RTC, never encoded once per viewer.
/// </summary>
public sealed class AudioPublishPipeline(
    IAudioCaptureSource capture,
    IAudioEncoder encoder,
    MediaSessionClock clock) : IAsyncDisposable
{
    // Capture callbacks jitter and can carry several frames, so timestamps follow the sample
    // count. Re-anchor only when capture stalled (loopback delivers nothing during silence).
    private static readonly TimeSpan ReanchorTolerance = TimeSpan.FromMilliseconds(100);

    private TimeSpan? _nextTimestamp;
    private bool _started;
    private bool _running;
    private bool _disposed;

    public event Action<EncodedAudioSample>? SampleEncoded;

    public event Action<Exception>? Failed;

    public string EncoderName => encoder.Name;

    public async Task StartAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) return;

        _started = true;
        _running = true;
        capture.AudioCaptured += OnAudioCaptured;

        try
        {
            await capture.StartAsync(ct);
        }
        catch
        {
            capture.AudioCaptured -= OnAudioCaptured;
            _running = false;
            _started = false;
            throw;
        }
    }

    public async Task StopAsync()
    {
        if (!_started) return;

        _started = false;
        _running = false;
        capture.AudioCaptured -= OnAudioCaptured;
        await capture.StopAsync();
    }

    private void OnAudioCaptured(AudioFrame frame)
    {
        if (!_running) return;

        var timestamp = NextTimestamp(frame.Duration);
        var stamped = new AudioFrame(
            frame.Data,
            frame.SampleRate,
            frame.Channels,
            frame.SampleCount,
            timestamp);

        EncodedAudioSample? encoded;
        try
        {
            encoded = encoder.Encode(stamped);
        }
        catch (Exception e)
        {
            _running = false;
            capture.AudioCaptured -= OnAudioCaptured;
            Failed?.Invoke(e);
            return;
        }

        if (encoded is not { } sample) return;

        SampleEncoded?.Invoke(sample with { Timestamp = timestamp });
    }

    private TimeSpan NextTimestamp(TimeSpan duration)
    {
        var now = clock.Now;
        // Never move backwards: running ahead of the clock (a burst after a re-anchor) is kept.
        var timestamp = _nextTimestamp is { } next && now - next <= ReanchorTolerance ? next : now;
        _nextTimestamp = timestamp + duration;
        return timestamp;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await StopAsync();
        await capture.DisposeAsync();
        encoder.Dispose();
    }
}
