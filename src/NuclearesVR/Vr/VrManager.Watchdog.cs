using System.Diagnostics;
using System.Threading;

namespace NuclearesVR.Vr
{
    /// <summary>
    /// Diagnostics for freezes. A background thread watches a frame counter that the main thread bumps every frame; if
    /// it stops moving for a few seconds, the log says which step of the mod was last reached. If that step is
    /// "WaitGetPoses" or "Submit" the freeze is in SteamVR; if it is "end of frame (mod done)" the game or graphics
    /// driver stopped somewhere outside the mod's own calls.
    /// </summary>
    internal partial class VrManager
    {
        private static volatile string _stage = "start";
        private static long _frameCounter;
        private static Thread _watchdog;
        private static volatile bool _watchdogStop;

        private static void Stage(string stage) => _stage = stage;

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
