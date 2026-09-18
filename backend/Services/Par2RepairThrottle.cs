namespace NzbWebDAV.Services;

/// <summary>
/// Bounds how many PAR2 repairs run at once. Each repair holds usenet connections
/// open while it downloads recovery slices, so an unbounded number of them would
/// starve streaming. Static because <see cref="Par2RepairService"/> is constructed
/// per call, from both the health check and the manual repair endpoint.
/// </summary>
public static class Par2RepairThrottle
{
    private static readonly Lock LockObj = new();
    private static int _inFlight;

    public static int InFlight
    {
        get { lock (LockObj) return _inFlight; }
    }

    /// <summary>
    /// Reserves a slot, or returns false when already at capacity. Refuses rather
    /// than waiting: a queued repair would block the health-check loop behind
    /// another item's download. A refused item retries at its next check.
    /// </summary>
    public static bool TryEnter(int maxConcurrent)
    {
        lock (LockObj)
        {
            if (_inFlight >= Math.Max(1, maxConcurrent)) return false;
            _inFlight++;
            return true;
        }
    }

    public static void Exit()
    {
        lock (LockObj)
        {
            // guard against a double-release from a bad finally block, which would
            // otherwise permanently inflate capacity.
            if (_inFlight > 0) _inFlight--;
        }
    }

    /// <summary>
    /// Clears the in-flight count. Public only because the test assembly has no
    /// InternalsVisibleTo grant; nothing in production should call it.
    /// </summary>
    public static void ResetForTests()
    {
        lock (LockObj) _inFlight = 0;
    }
}
