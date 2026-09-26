using UnityEngine;

namespace NuclearesVR.Vr
{
    /// <summary>
    /// Shows where the mouse is. The OS cursor isn't part of ScreenCapture's
    /// output and isn't rendered by any camera, so it's invisible in the
    /// headset. Two stand-ins, both on the mirror layer so only the eye
    /// cameras draw them (never the monitor):
    ///   - on the virtual screen (menus, pause menu): an arrow at the mouse's
    ///     position on the captured screen;
    ///   - in the world (tablet, ALT interactive mode): a marker where the
    ///     game's own click ray - main camera through the mouse position -
    ///     lands, which is exactly what a click would hit.
    /// Both only show while the game has the OS cursor visible.
    /// </summary>
    internal partial class VrManager
    {
        private GameObject _cursorArrow;
        private GameObject _worldMarker;
        private bool _pointerStateLogged;
        private bool _lastCursorVisible;

        private const float CursorSize = 0.07f;

        private static Material MakeUnlitColor(Color color)
        {
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.SetPixel(0, 0, color);
            tex.Apply();
            return new Material(Shader.Find("Unlit/Texture")) { mainTexture = tex };
        }

        private GameObject MakeArrowPart(string name, Material material)
        {
            var mesh = new Mesh();
            mesh.vertices = new[]
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(0.6f, -0.7f, 0f),
                new Vector3(0f, -1f, 0f)
            };
            // Both windings, so it's visible whichever way it ends up facing.
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 1 };
            mesh.RecalculateBounds();

            var go = new GameObject(name);
            go.layer = _mirrorLayer;
            go.AddComponent<MeshFilter>().mesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.material = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return go;
        }

        private void SetUpPointerObjects()
        {
            if (_cursorArrow == null)
            {
                _cursorArrow = MakeArrowPart("NuclearesVR_CursorArrow", MakeUnlitColor(Color.white));
                _cursorArrow.transform.localScale = Vector3.one * CursorSize;

                // Dark outline: the same arrow, larger and just behind, shifted
                // so the tip stays put (scaling about the arrow's centroid).
                var outline = MakeArrowPart("Outline", MakeUnlitColor(Color.black));
                outline.transform.SetParent(_cursorArrow.transform, false);
                outline.transform.localScale = Vector3.one * 1.35f;
                outline.transform.localPosition = new Vector3(-0.07f, 0.198f, 0.02f);
                _cursorArrow.SetActive(false);
            }

            if (_worldMarker == null)
            {
                _worldMarker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                _worldMarker.name = "NuclearesVR_WorldMarker";
                _worldMarker.layer = _mirrorLayer;
                var collider = _worldMarker.GetComponent<Collider>();
                if (collider != null)
                {
                    Destroy(collider);
                }
                var renderer = _worldMarker.GetComponent<MeshRenderer>();
                renderer.material = MakeUnlitColor(new Color(1f, 0.15f, 0.1f));
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                _worldMarker.SetActive(false);
            }
        }

        private void ParentMirrorObjects(Transform camera)
        {
            if (_mirrorQuad != null)
            {
                _mirrorQuad.transform.SetParent(camera, worldPositionStays: false);
                _mirrorQuad.transform.localPosition = new Vector3(0f, MirrorScreenOffsetY, MirrorScreenDistance);
                _mirrorQuad.transform.localRotation = Quaternion.identity;
            }
            ParentInfoPanel(camera);
            if (_cursorArrow != null)
            {
                _cursorArrow.transform.SetParent(camera, worldPositionStays: false);
                _cursorArrow.transform.localRotation = Quaternion.identity;
            }
        }

        private void UpdatePointerVisuals()
        {
            var cursorVisible = Cursor.visible;
            if (!_pointerStateLogged || cursorVisible != _lastCursorVisible)
            {
                _pointerStateLogged = true;
                _lastCursorVisible = cursorVisible;
                Plugin.Logger.LogInfo($"OS cursor visible={cursorVisible}, lockState={Cursor.lockState}");
            }

            var showArrow = _cursorArrow != null && _mirrorVisible && cursorVisible;
            if (_cursorArrow != null && _cursorArrow.activeSelf != showArrow)
            {
                _cursorArrow.SetActive(showArrow);
            }
            if (showArrow && Screen.width > 0 && Screen.height > 0)
            {
                var u = Mathf.Clamp01(Input.mousePosition.x / Screen.width);
                var v = Mathf.Clamp01(Input.mousePosition.y / Screen.height);
                var width = MirrorScreenHeight * Screen.width / Screen.height;
                _cursorArrow.transform.localPosition =
                    new Vector3((u - 0.5f) * width, (v - 0.5f) * MirrorScreenHeight + MirrorScreenOffsetY, MirrorScreenDistance - 0.003f);
            }

            var showMarker = false;
            // The controllers' lasers replace this marker; it stays as the fallback when no
            // controller is being tracked (mouse and keyboard play).
            var controllersPointing = _inputReady && Plugin.PointingEnabled.Value &&
                                      (LeftHand.PoseValid || RightHand.PoseValid);
            if (_worldMarker != null && !_mirrorVisible && cursorVisible && _mainCamera != null && !controllersPointing)
            {
                var ray = _mainCamera.ScreenPointToRay(Input.mousePosition);
                var mask = _mirrorLayer >= 0 ? ~(1 << _mirrorLayer) : ~0;
                if (Physics.Raycast(ray, out var hit, 15f, mask, QueryTriggerInteraction.Collide))
                {
                    showMarker = true;
                    var size = 0.012f * Mathf.Max(hit.distance, 1f);
                    _worldMarker.transform.position = hit.point + hit.normal * (size * 0.3f);
                    _worldMarker.transform.localScale = Vector3.one * size;
                }
            }
            if (_worldMarker != null && _worldMarker.activeSelf != showMarker)
            {
                _worldMarker.SetActive(showMarker);
            }
        }
    }
}
