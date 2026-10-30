using System.Diagnostics;

namespace AVSync;

/// <summary>A clock reporting local wall-clock time as <see cref="DateTime"/> ticks (100 ns units).</summary>
public interface IClock
{
    long NowTicks { get; }
}

/// <summary>Monotonic high-resolution time (QueryPerformanceCounter) in 100 ns ticks. Never jumps.</summary>
public static class Mono
{
    private static readonly long Freq = Stopwatch.Frequency;

    public static long NowTicks
    {
        get
        {
            long ts = Stopwatch.GetTimestamp();
            if (Freq == TimeSpan.TicksPerSecond) return ts;
            return ts / Freq * TimeSpan.TicksPerSecond + ts % Freq * TimeSpan.TicksPerSecond / Freq;
        }
    }
}

/// <summary>This computer's local clock (precise system time + time zone offset). Used by the source.</summary>
public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    private long _utcOffset;
    private long _nextOffsetCheck;

    public long NowTicks
    {
        get
        {
            // On Windows 8+ DateTime.UtcNow uses GetSystemTimePreciseAsFileTime (sub-microsecond resolution).
            long utc = DateTime.UtcNow.Ticks;
            if (utc >= Volatile.Read(ref _nextOffsetCheck))
            {
                Volatile.Write(ref _utcOffset, TimeZoneInfo.Local.GetUtcOffset(new DateTime(utc, DateTimeKind.Utc)).Ticks);
                Volatile.Write(ref _nextOffsetCheck, utc + 10 * TimeSpan.TicksPerSecond);
            }
            return utc + Volatile.Read(ref _utcOffset);
        }
    }
}

/// <summary>Monotonic clock plus an offset that is steered toward the source's clock. Used by the sync side.</summary>
public sealed class SyncedClock : IClock
{
    private const long StepThreshold = 50 * TimeSpan.TicksPerMillisecond;

    private long _offset = SystemClock.Instance.NowTicks - Mono.NowTicks;
    private bool _locked;

    public long NowTicks => Mono.NowTicks + Interlocked.Read(ref _offset);

    public bool Locked => Volatile.Read(ref _locked);

    /// <summary>Steers the clock toward <paramref name="target"/> (source time minus monotonic time).</summary>
    public void Steer(long target)
    {
        long current = Interlocked.Read(ref _offset);
        long diff = target - current;
        // Step on first lock or a large error; otherwise slew gently so the display never visibly jumps.
        long next = !_locked || Math.Abs(diff) > StepThreshold ? target : current + diff / 8;
        Interlocked.Exchange(ref _offset, next);
        Volatile.Write(ref _locked, true);
    }
}
