using TMPro;
using UnityEngine;

namespace NuclearesVR.Vr
{
    /// <summary>
    /// The in-game monitors' text is 3D TextMeshPro sitting at essentially the same depth as the screen
    /// surface behind it. With the eye cameras in Forward, which of the two is drawn last (and so wins
    /// the tie) changed with the viewpoint, so text popped on and off and swept across the screen as the
    /// head moved. The fix is to draw 3D text after all other solid geometry (render queue 2500), so it
    /// always wins, with the game's own shader and depth testing otherwise untouched. The game's own
    /// Deferred path never hit this ordering.
    /// </summary>
    internal partial class VrManager
    {
        internal static bool DrawTextLate = true;
        private const int LateTextQueue = 2500;
        private float _nextLateTextScan;

        private void UpdateLateText()
        {
            if (!DrawTextLate || PlayerLook.Instancia == null || Time.unscaledTime < _nextLateTextScan)
            {
                return;
            }
            _nextLateTextScan = Time.unscaledTime + 3f;

            var changed = 0;
            foreach (var text in FindObjectsOfType<TextMeshPro>(true))
            {
                var material = text.fontSharedMaterial;
                if (material != null && material.renderQueue == 2000)
                {
                    material.renderQueue = LateTextQueue;
                    changed++;
                }
            }
            if (changed > 0)
            {
                Plugin.Logger.LogInfo($"Moved {changed} 3D text material(s) to render queue {LateTextQueue} (draws after the surface behind it).");
            }
        }

        private void UpdateMonitorTextShaders()
        {
            UpdateLateText();
        }
    }
}
