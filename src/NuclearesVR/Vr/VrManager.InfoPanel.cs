using System;
using HarmonyLib;
using UnityEngine;

namespace NuclearesVR.Vr
{
    /// <summary>
    /// The game's "component information" box (the panel that appears when the right mouse button is
    /// held on a gauge or switch) lives on the screen overlay, which no camera can see, so it never
    /// showed in the headset. While it is open, the part of the captured screen it occupies is shown on
    /// a small panel in front of the player, below eye level, that only the eye cameras draw.
    /// </summary>
    internal partial class VrManager
    {
        private GameObject _infoQuad;
        private Material _infoMaterial;
        private bool _infoVisible;
        private readonly Vector3[] _infoCorners = new Vector3[4];

        private static readonly System.Reflection.FieldInfo PanelInfoField = AccessTools.Field(typeof(Interface), "PanelIEP");

        private const float InfoPanelWidth = 0.9f;
        private static readonly Vector3 InfoPanelPosition = new Vector3(0f, -0.33f, 1.1f);

        private void UpdateInfoPanel()
        {
            var show = false;
            try
            {
                show = TryPlaceInfoPanel();
            }
            catch (Exception ex)
            {
                LogThrottled("info-panel-error", $"Info panel error: {ex.Message}");
            }
            _infoVisible = show;
            if (_infoQuad != null && _infoQuad.activeSelf != show)
            {
                _infoQuad.SetActive(show);
            }
        }

        private bool TryPlaceInfoPanel()
        {
            if (!_inputReady || _mirrorVisible || _mirrorTex == null || _mainCamera == null ||
                PanelInfoField == null || Interface.Instancia == null || Screen.width <= 0 || Screen.height <= 0)
            {
                return false;
            }
            var group = PanelInfoField.GetValue(Interface.Instancia) as CanvasGroup;
            if (group == null || !group.gameObject.activeInHierarchy || group.alpha < 0.01f)
            {
                return false;
            }
            var rect = group.transform as RectTransform;
            var canvas = group.GetComponentInParent<Canvas>();
            if (rect == null || canvas == null)
            {
                return false;
            }

            rect.GetWorldCorners(_infoCorners);
            var camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            var bottomLeft = RectTransformUtility.WorldToScreenPoint(camera, _infoCorners[0]);
            var topRight = RectTransformUtility.WorldToScreenPoint(camera, _infoCorners[2]);
            var x0 = Mathf.Clamp01(bottomLeft.x / Screen.width);
            var x1 = Mathf.Clamp01(topRight.x / Screen.width);
            var y0 = Mathf.Clamp01(bottomLeft.y / Screen.height);
            var y1 = Mathf.Clamp01(topRight.y / Screen.height);
            if (x1 - x0 < 0.01f || y1 - y0 < 0.01f)
            {
                return false;
            }

            EnsureInfoQuad();

            // The captured texture is stored upside down, so the vertical range runs backwards
            // (same flip as the full-screen menu quad, restricted to this rectangle).
            _infoMaterial.mainTextureScale = new Vector2(x1 - x0, -(y1 - y0));
            _infoMaterial.mainTextureOffset = new Vector2(x0, 1f - y0);

            var aspect = ((x1 - x0) * Screen.width) / ((y1 - y0) * Screen.height);
            _infoQuad.transform.localScale = new Vector3(InfoPanelWidth, InfoPanelWidth / aspect, 1f);
            return true;
        }

        private void EnsureInfoQuad()
        {
            if (_infoQuad != null)
            {
                return;
            }
            _infoQuad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _infoQuad.name = "NuclearesVR_InfoPanel";
            _infoQuad.layer = _mirrorLayer;
            var collider = _infoQuad.GetComponent<Collider>();
            if (collider != null)
            {
                Destroy(collider);
            }
            var shader = Shader.Find("UI/Default") ?? Shader.Find("Unlit/Texture");
            _infoMaterial = new Material(shader) { mainTexture = _mirrorTex };
            if (_infoMaterial.HasProperty("unity_GUIZTestMode"))
            {
                _infoMaterial.SetInt("unity_GUIZTestMode", 8); // draw over the scene, never hidden by it
            }
            _infoMaterial.renderQueue = 4000;
            var renderer = _infoQuad.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = _infoMaterial;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            ParentInfoPanel(_mainCamera.transform);
            _infoQuad.SetActive(false);
        }

        private void ParentInfoPanel(Transform camera)
        {
            if (_infoQuad == null)
            {
                return;
            }
            _infoQuad.transform.SetParent(camera, worldPositionStays: false);
            _infoQuad.transform.localPosition = InfoPanelPosition;
            _infoQuad.transform.localRotation = Quaternion.identity;
        }

        private void HideInfoPanel()
        {
            _infoVisible = false;
            if (_infoQuad != null)
            {
                _infoQuad.SetActive(false);
            }
        }
    }
}
