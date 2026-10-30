using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AVSync;

/// <summary>
/// Plays a sharp tick exactly on each second of <see cref="Clock"/> (a lower, longer tone on the minute).
/// Audio is streamed continuously through WASAPI; on every buffer fill we work out when that buffer will reach
/// the speakers (from the device's play position) and place the tick at the exact sample that lines up with the
/// second boundary. This avoids the large, variable start-up latency of "play a sound now" APIs.
/// </summary>
public sealed class TickAudio : ISampleProvider, IDisposable
{
    private const int SampleRate = 48000;
    private const double TicksPerFrame = (double)TimeSpan.TicksPerSecond / SampleRate;

    private static readonly float[] SecondTick = Tone(3000, decayMs: 1.5, lengthMs: 10, amplitude: 0.9f);
    private static readonly float[] MinuteTick = Tone(1500, decayMs: 25, lengthMs: 120, amplitude: 0.9f);

    private WasapiOut? _output;
    private long _framesWritten;
    private long _lastBoundary = long.MinValue;
    private float[]? _current;
    private int _currentPos;

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2);

    public volatile IClock Clock;
    public volatile bool Enabled;

    /// <summary>Positive values make the tick play later, negative earlier (to line up with the camera).</summary>
    public double OffsetMs { get; set; }

    public string? Error { get; private set; }

    public TickAudio(IClock clock)
    {
        Clock = clock;
    }

    public void Start()
    {
        // Create on an MTA thread-pool thread; WASAPI objects are then used from NAudio's MTA playback thread.
        Task.Run(() =>
        {
            try
            {
                var output = new WasapiOut(AudioClientShareMode.Shared, useEventSync: true, latency: 30);
                output.Init(this);
                output.PlaybackStopped += (_, e) => Error = e.Exception?.Message ?? "audio stopped";
                _output = output;
                output.Play();
            }
            catch (Exception ex)
            {
                Error = ex.Message;
            }
        });
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / 2;

        // When will the first sample of this buffer be heard? Now + whatever is already queued ahead of it.
        double queuedSec = _framesWritten / (double)SampleRate - PlayedSeconds();
        if (queuedSec < 0 || queuedSec > 1) queuedSec = 0;
        long start = Clock.NowTicks + (long)(queuedSec * TimeSpan.TicksPerSecond)
                     - (long)(OffsetMs * TimeSpan.TicksPerMillisecond);
        long end = start + (long)(frames * TicksPerFrame);

        // Latest whole second inside this buffer (or one that only just slipped past, e.g. due to jitter).
        long boundary = (end - 1) / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond;
        if (boundary > _lastBoundary && boundary >= start - 20 * TimeSpan.TicksPerMillisecond)
        {
            _lastBoundary = boundary;
            if (Enabled)
            {
                bool minute = boundary / TimeSpan.TicksPerSecond % 60 == 0;
                _current = minute ? MinuteTick : SecondTick;
                _currentPos = -(int)(Math.Max(0, boundary - start) / TicksPerFrame);
            }
        }

        var tick = _current;
        for (int i = 0; i < frames; i++)
        {
            float s = 0;
            if (tick != null)
            {
                int p = _currentPos + i;
                if (p >= 0 && p < tick.Length) s = tick[p];
            }
            buffer[offset + 2 * i] = s;
            buffer[offset + 2 * i + 1] = s;
        }
        if (tick != null)
        {
            _currentPos += frames;
            if (_currentPos >= tick.Length) _current = null;
        }

        _framesWritten += frames;
        return frames * 2;
    }

    private double PlayedSeconds()
    {
        var output = _output;
        if (output == null) return 0;
        try
        {
            return output.GetPosition() / (double)output.OutputWaveFormat.AverageBytesPerSecond;
        }
        catch
        {
            return _framesWritten / (double)SampleRate - 0.03;
        }
    }

    private static float[] Tone(double freq, double decayMs, double lengthMs, float amplitude)
    {
        var samples = new float[(int)(SampleRate * lengthMs / 1000)];
        for (int i = 0; i < samples.Length; i++)
        {
            double t = i / (double)SampleRate;
            samples[i] = (float)(amplitude * Math.Sin(2 * Math.PI * freq * t) * Math.Exp(-t * 1000 / decayMs));
        }
        return samples;
    }

    public void Dispose()
    {
        try
        {
            _output?.Stop();
            _output?.Dispose();
        }
        catch { }
    }
}
