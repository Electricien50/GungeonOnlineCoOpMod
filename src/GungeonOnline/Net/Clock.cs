namespace GungeonOnline.Net
{
    /// <summary>
    /// Seconds since the game started, exact to the microsecond however long the game has been open. The game's own
    /// clock is a 32-bit float: after 6 hours it moves in 2 ms steps, after a day in 8 ms steps, and network timing
    /// read from it would see that as jitter. Lined up with Time.realtimeSinceStartup when the mod loads, so the two
    /// can still be compared.
    /// </summary>
    public static class Clock
    {
        static readonly System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        static readonly double start = UnityEngine.Time.realtimeSinceStartup;
        public static double Now => start + watch.Elapsed.TotalSeconds;
        public static double NowMs => Now * 1000.0;
    }
}
