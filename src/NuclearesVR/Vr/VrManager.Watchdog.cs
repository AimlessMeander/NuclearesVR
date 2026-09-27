using System.Diagnostics;
using System.Text;
using System.Threading;

namespace NuclearesVR.Vr
{
    /// <summary>
    /// Diagnostics for freezes. Two mechanisms:
    /// 1. A background thread watches a frame counter the main thread bumps every frame; if it stops moving for a
    ///    few seconds, the log says which step was last reached (a full freeze - the game, SteamVR or the graphics
    ///    driver stopped responding).
    /// 2. Every step is individually timed (sub-millisecond). A step that alone takes far longer than its normal
    ///    sub-millisecond cost is logged immediately, with the steps just before it, since a full freeze only shows
    ///    up 3 seconds later but a brief stall (a fraction of a second) can be the actual trigger - SteamVR's Steam
    ///    Link video stream has been seen to lose sync and fail to recover after such a stall elsewhere in the
    ///    system, even though a monitor or a cabled headset would not show it at all.
    /// </summary>
    internal partial class VrManager
    {
        private static volatile string _stage = "start";
        private static long _frameCounter;
        private static Thread _watchdog;
        private static volatile bool _watchdogStop;

        private static readonly Stopwatch StageClock = Stopwatch.StartNew();
        private static double _stageStartMs;
        private const double StallThresholdMs = 25.0; // a single step normally costs well under 1 ms

        private struct StageRecord
        {
            public string Name;
            public double Ms;
        }

        private const int HistoryLength = 40;
        private static readonly StageRecord[] History = new StageRecord[HistoryLength];
        private static int _historyNext;
        private static double _lastStallLogMs = double.NegativeInfinity;

        private static void Stage(string stage)
        {
            var now = StageClock.Elapsed.TotalMilliseconds;
            var elapsed = now - _stageStartMs;
            var previous = _stage;
            History[_historyNext] = new StageRecord { Name = previous, Ms = elapsed };
            _historyNext = (_historyNext + 1) % HistoryLength;
            if (elapsed > StallThresholdMs && now - _lastStallLogMs > 500.0)
            {
                _lastStallLogMs = now;
                var sb = new StringBuilder();
                sb.Append($"[stall] '{previous}' took {elapsed:F1} ms (normally under 1 ms). Steps just before it: ");
                for (var i = 0; i < HistoryLength; i++)
                {
                    var rec = History[(_historyNext + i) % HistoryLength];
                    if (rec.Name == null) continue;
                    sb.Append($"{rec.Name}={rec.Ms:F1}ms, ");
                }
                Plugin.Logger.LogWarning(sb.ToString());
            }
            _stageStartMs = now;
            _stage = stage;
        }

        private static void StartWatchdog()
        {
            if (_watchdog != null)
            {
                return;
            }
            _watchdogStop = false;
            _watchdog = new Thread(WatchdogLoop) { IsBackground = true, Name = "NuclearesVR watchdog" };
            _watchdog.Start();
        }

        private static void StopWatchdog()
        {
            _watchdogStop = true;
            _watchdog = null;
        }

        private static void WatchdogLoop()
        {
            var clock = Stopwatch.StartNew();
            var lastCounter = -1L;
            var lastChange = clock.Elapsed.TotalSeconds;
            var nextReport = 0.0;
            while (!_watchdogStop)
            {
                Thread.Sleep(500);
                var counter = Interlocked.Read(ref _frameCounter);
                var now = clock.Elapsed.TotalSeconds;
                if (counter != lastCounter)
                {
                    if (nextReport > 0.0)
                    {
                        Plugin.Logger.LogWarning($"[watchdog] the game is running again after {now - lastChange:F1} s.");
                    }
                    lastCounter = counter;
                    lastChange = now;
                    nextReport = 0.0;
                }
                else if (now - lastChange >= 3.0 && now >= nextReport)
                {
                    Plugin.Logger.LogWarning($"[watchdog] no frame for {now - lastChange:F1} s; last step reached: {_stage}");
                    nextReport = now + 5.0;
                }
            }
        }
    }
}
