using UnityEngine;

namespace NuclearesVR.Vr
{
    /// <summary>The per-eye render textures the headset views are drawn into.</summary>
    internal partial class VrManager
    {
        private int _recommendedWidth, _recommendedHeight;
        private float _headsetHz = 90f;
        private float _currentScale = 1f;
        private int _currentMsaa = 4;

        private RenderTexture MakeEyeTexture(float scale, int msaa)
        {
            var width = Mathf.Max(64, Mathf.RoundToInt(_recommendedWidth * scale / 8f) * 8);
            var height = Mathf.Max(64, Mathf.RoundToInt(_recommendedHeight * scale / 8f) * 8);
            var texture = new RenderTexture(width, height, 24, RenderTextureFormat.Default) { antiAliasing = msaa };
            texture.Create();
            return texture;
        }
    }
}
