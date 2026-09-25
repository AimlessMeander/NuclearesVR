using UnityEngine;
using UnityEngine.Rendering;

namespace NuclearesVR.Vr
{
    /// <summary>
    /// Shows the headset's view (the left eye) in the game window, as most VR games do, instead of the
    /// black window that "LightweightMonitorView" would otherwise leave. The game's own camera draws
    /// nothing in VR, so this costs one full-screen copy of a texture that already exists.
    ///
    /// It is a command buffer on the game camera, run after everything else the camera does but before
    /// the game's screen-space UI is drawn, so the HUD still appears over it, and so the gauge
    /// information panel (which is captured from the screen) has the picture behind it. The eye texture
    /// is cropped to the window's shape rather than stretched. Not used while the virtual menu screen is
    /// up: then the game camera draws the real scene itself.
    /// </summary>
    internal partial class VrManager
    {
        private CommandBuffer _monitorMirrorBuffer;
        private Camera _monitorMirrorCamera;
        private RenderTexture _monitorMirrorSource;
        private float _monitorMirrorAspect;

        private void UpdateMonitorMirror()
        {
            var want = Plugin.MonitorShowsHeadset.Value && Plugin.LightweightMonitorView.Value && !_mirrorVisible &&
                       _mainCamera != null && _leftTex != null && _leftTex.IsCreated() && Screen.height > 0;
            if (!want)
            {
                RemoveMonitorMirror();
                return;
            }

            var windowAspect = (float)Screen.width / Screen.height;
            if (_monitorMirrorBuffer != null && _monitorMirrorCamera == _mainCamera && _monitorMirrorSource == _leftTex &&
                Mathf.Approximately(_monitorMirrorAspect, windowAspect))
            {
                return;
            }

            RemoveMonitorMirror();

            // Crop the (roughly square) eye picture to the window's shape, centred.
            var eyeAspect = (float)_leftTex.width / _leftTex.height;
            var scale = Vector2.one;
            var offset = Vector2.zero;
            if (windowAspect > eyeAspect)
            {
                scale.y = eyeAspect / windowAspect;
                offset.y = (1f - scale.y) / 2f;
            }
            else
            {
                scale.x = windowAspect / eyeAspect;
                offset.x = (1f - scale.x) / 2f;
            }

            _monitorMirrorBuffer = new CommandBuffer { name = "NuclearesVR.MonitorMirror" };
            _monitorMirrorBuffer.Blit(_leftTex, BuiltinRenderTextureType.CameraTarget, scale, offset);
            _mainCamera.AddCommandBuffer(CameraEvent.AfterEverything, _monitorMirrorBuffer);
            _monitorMirrorCamera = _mainCamera;
            _monitorMirrorSource = _leftTex;
            _monitorMirrorAspect = windowAspect;
        }

        private void RemoveMonitorMirror()
        {
            if (_monitorMirrorBuffer == null)
            {
                return;
            }
            if (_monitorMirrorCamera != null)
            {
                _monitorMirrorCamera.RemoveCommandBuffer(CameraEvent.AfterEverything, _monitorMirrorBuffer);
            }
            _monitorMirrorBuffer.Release();
            _monitorMirrorBuffer = null;
            _monitorMirrorCamera = null;
            _monitorMirrorSource = null;
        }
    }
}
