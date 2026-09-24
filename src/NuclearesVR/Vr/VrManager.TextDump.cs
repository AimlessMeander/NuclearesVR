using System;
using System.Linq;
using TMPro;
using UnityEngine;

namespace NuclearesVR.Vr
{
    /// <summary>
    /// Ctrl+Shift+X: for every 3D TextMeshPro within 25 m in front of the head,
    /// logs whether it is enabled, whether Unity considers it visible, whether
    /// it falls inside each eye camera's frustum, its layer against the eye
    /// culling mask, and the mesh/material state that could hide it. Take one
    /// dump where the monitor text shows and one where it doesn't, and diff.
    /// </summary>
    internal partial class VrManager
    {
        private void DumpTextState()
        {
            try
            {
                var log = Plugin.Logger;
                if (_mainCamera == null || _leftEyeCamera == null)
                {
                    log.LogInfo("Text dump: no cameras yet.");
                    return;
                }
                var head = _leftEyeCamera.transform;
                log.LogInfo($"=== text dump: head at {head.position} forward {head.forward}; " +
                            $"eyeMask={_leftEyeCamera.cullingMask} near={_leftEyeCamera.nearClipPlane} far={_leftEyeCamera.farClipPlane} " +
                            $"path={_leftEyeCamera.actualRenderingPath} mainPath={_mainCamera.actualRenderingPath} " +
                            $"lodBias={QualitySettings.lodBias} maxLOD={QualitySettings.maximumLODLevel} ===");

                var leftPlanes = GeometryUtility.CalculateFrustumPlanes(_leftEyeCamera);
                var rightPlanes = GeometryUtility.CalculateFrustumPlanes(_rightEyeCamera);
                var mainPlanes = GeometryUtility.CalculateFrustumPlanes(_mainCamera);

                log.LogInfo($"  TMP_Text objects in scene: {FindObjectsOfType<TMP_Text>(true).Length} (TextMeshPro 3D: {FindObjectsOfType<TextMeshPro>(true).Length}, UI: {FindObjectsOfType<TextMeshProUGUI>(true).Length})");
                log.LogInfo("  (nearest 3D text within 25 m, no filtering except the player's own objects)");
                var shown = 0;
                foreach (var text in FindObjectsOfType<TMP_Text>(true)
                             .OrderBy(t => (t.transform.position - head.position).sqrMagnitude))
                {
                    var offset = text.transform.position - head.position;
                    if (offset.magnitude > 25f || !text.gameObject.activeInHierarchy || text.textInfo == null || text.textInfo.characterCount == 0 || text.transform.root.name.StartsWith("Jugador") ||
                        text.transform.GetComponentInParent<Canvas>() != null && text is TextMeshProUGUI)
                    {
                        continue;
                    }
                    if (++shown > 40)
                    {
                        break;
                    }
                    var r = text.GetComponent<Renderer>();
                    var b = r != null ? r.bounds : new Bounds();
                    var vp = _leftEyeCamera.WorldToViewportPoint(b.center);
                    var lod = text.GetComponentInParent<LODGroup>();
                    log.LogInfo($"  {Path(text.transform)} active={text.gameObject.activeInHierarchy} rEnabled={(r != null && r.enabled)} " +
                                $"isVisible={(r != null && r.isVisible)} layer={text.gameObject.layer} " +
                                $"inEyeMask={(_leftEyeCamera.cullingMask & (1 << text.gameObject.layer)) != 0} dist={offset.magnitude:F2}");
                    log.LogInfo($"    chars={(text.textInfo != null ? text.textInfo.characterCount : -1)} str='{(text.text.Length > 24 ? text.text.Substring(0, 24) : text.text)}' type={text.GetType().Name} verts={(text.mesh != null ? text.mesh.vertexCount : -1)} alpha={text.alpha:F2} " +
                                $"color={text.color} bounds={b.center}/{b.size} viewportL=({vp.x:F2},{vp.y:F2},{vp.z:F2}) " +
                                $"inFrustum L={GeometryUtility.TestPlanesAABB(leftPlanes, b)} R={GeometryUtility.TestPlanesAABB(rightPlanes, b)} main={GeometryUtility.TestPlanesAABB(mainPlanes, b)} " +
                                $"lod={(lod != null ? lod.name + " lods=" + lod.lodCount : "none")} sortOrder={(r != null ? r.sortingOrder : 0)} shadows={(r != null ? r.shadowCastingMode.ToString() : "-")}");
                    if (r != null)
                    {
                        log.LogInfo($"    material {MaterialSummary(r.sharedMaterial)}");
                    }
                }
                if (shown == 0)
                {
                    log.LogInfo("  no 3D text within 25 m of you.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"Text dump failed: {ex}");
            }
        }
    }
}
