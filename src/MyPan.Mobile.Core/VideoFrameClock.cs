namespace MyPan.Mobile.Core;

// Camera timestamps may use BOOTTIME (including suspend), whereas MediaRecorder uses MONOTONIC.
// Map sensor deltas into the encoder clock; never send the camera's absolute boot timestamp.
public sealed class VideoFrameClock
{
    private bool initialized;
    private long sensorBase, monotonicBase, last;
    public void Reset() { initialized = false; last = long.MinValue; }
    public long Timestamp(long sensorNanoseconds, long monotonicNanoseconds)
    {
        var sensorDelta = sensorNanoseconds - sensorBase;
        var elapsed = monotonicNanoseconds - monotonicBase;
        if (!initialized || sensorDelta < 0 || Math.Abs(sensorDelta - elapsed) > 2_000_000_000L)
        {
            initialized = true; sensorBase = sensorNanoseconds; monotonicBase = monotonicNanoseconds;
            sensorDelta = 0;
        }
        var mapped = monotonicBase + sensorDelta;
        last = last == long.MinValue ? mapped : Math.Max(last + 1, mapped);
        return last;
    }
}
