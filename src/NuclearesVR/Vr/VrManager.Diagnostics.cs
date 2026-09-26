using UnityEngine;

namespace NuclearesVR.Vr
{
    /// <summary>
    /// Hotkeys for finding performance problems. Both only write [bench] / [probe] lines to the log:
    /// Ctrl+Shift+B measures the GPU cost of the view with parts of the scene switched off in turn, and
    /// Ctrl+Shift+V lists what is being drawn and what happens to the cost when each group is hidden.
    /// </summary>
    internal partial class VrManager
    {
        // The eye cameras render in Forward. That makes the torch and alarm lights work in the headset,
        // which Deferred did not (see VrManager.MonitorText.cs for how the in-game monitors are handled).
        internal static bool ForwardOnEyes = true;

        private void CheckDiagnosticsKey()
        {
            var ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            var shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (!ctrl || !shift)
            {
                return;
            }
            if (Input.GetKeyDown(KeyCode.B))
            {
                StartBenchmark();
            }
            if (Input.GetKeyDown(KeyCode.V))
            {
                StartViewProbe();
            }
        }
    }
}
