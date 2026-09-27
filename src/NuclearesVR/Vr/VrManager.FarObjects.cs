using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Valve.VR;

namespace NuclearesVR.Vr
{
    /// <summary>
    /// Far-away small objects. The plant has tens of thousands of separate small objects (bolts, pipe
    /// fittings, lamps, decorations), and from many places the headset sees thousands of them well over
    /// 100 m away, each one a separate draw call, drawn once per eye. Measured at the worst view: the
    /// 3,000 objects more than 100 m away cost about 6 ms of an 11 ms frame; the walls and other big
    /// things are few and cost little.
    ///
    /// Objects whose bounds are small (under a set size) are switched off (forceRenderingOff) while the
    /// NEAREST point of their bounds is farther than a set distance, and come back as you approach.
    /// Big objects (walls, large machines) are untouched here and keep the draw distance set elsewhere.
    /// Bounds are cached when the list is built (every minute), so the per-frame work is arithmetic
    /// on a few thousand entries at a time.
    /// </summary>
    internal partial class VrManager
    {
        private Renderer[] _farCandidates = new Renderer[0];
        private Vector3[] _farCenters = new Vector3[0];
        private Vector3[] _farExtents = new Vector3[0];
        private bool[] _farHidden = new bool[0];
        private int _farHiddenCount;
        private Coroutine _farScan;
        private float _nextFarScan;
        private int _farCursor;
        private int _farStaleLogged;
        private int[] _farVerts = new int[0];

        // Adaptive detail: when the GPU is over its target, objects whose geometry is far denser than the pixels
        // they cover on screen (tiny, very detailed nuts, fixtures and pump parts in the distance) are left undrawn,
        // densest first; the limit relaxes again when there is headroom. float.MaxValue = nothing extra is hidden.
        private float _densityLimit = float.MaxValue;
        private float _gpuAverage = -1f;
        private float _nextDensityStep;
        private const int DensityMinVertices = 3000;
        private const float DensityMinDistanceSquared = 16f; // never within 4 m of you

        private void UpdateFarSmallObjects()
        {
            var distance = Plugin.FarSmallObjectDistance.Value;
            if (distance <= 0f || _leftEyeCamera == null || PlayerLook.Instancia == null)
            {
                RestoreFarSmallObjects();
                return;
            }

            if (_farScan == null && Time.unscaledTime >= _nextFarScan)
            {
                _nextFarScan = Time.unscaledTime + 60f; // areas load and unload
                _farScan = StartCoroutine(ScanFarCandidates());
            }
            var count = _farCandidates.Length;
            if (count == 0)
            {
                return;
            }

            var p = _leftEyeCamera.transform.position;
            var limitSquared = distance * distance;
            var tanLimit = Mathf.Tan(Plugin.FarSmallObjectMaxAngle.Value * Mathf.Deg2Rad);
            UpdateDensityLimit();
            var pixelsPerTan = _leftEyeCamera.projectionMatrix.m00 * (_leftTex != null ? _leftTex.width : 2064) * 0.5f;
            var pixelScale = 0.5f * pixelsPerTan * pixelsPerTan; // pixels covered ~ this * size^2 / distance^2
            var slice = count / 6 + 1; // every object is re-checked about every 6 frames
            for (var n = 0; n < slice; n++)
            {
                if (_farCursor >= count)
                {
                    _farCursor = 0;
                }
                var i = _farCursor++;

                var c = _farCenters[i];
                var e = _farExtents[i];
                var far = IsFar(p, c, e, limitSquared, tanLimit, _farVerts[i], _densityLimit, pixelScale);
                var renderer = _farCandidates[i];
                if (far && renderer != null)
                {
                    // The bounds were cached when the list was built. Some objects (parts that are built or moved
                    // after that) had different bounds then. Before hiding one, look at where it is NOW.
                    var fresh = renderer.bounds;
                    if ((fresh.center - c).sqrMagnitude > 4f || (fresh.extents - e).sqrMagnitude > 4f)
                    {
                        if (_farStaleLogged++ < 12)
                        {
                            Plugin.Logger.LogInfo($"Far objects: '{renderer.name}' had out-of-date bounds (centre was {c}, is {fresh.center}).");
                        }
                        _farCenters[i] = c = fresh.center;
                        _farExtents[i] = e = fresh.extents;
                        far = IsFar(p, c, e, limitSquared, tanLimit, _farVerts[i], _densityLimit, pixelScale);
                    }
                }
                if (far == _farHidden[i] || renderer == null)
                {
                    continue;
                }
                renderer.forceRenderingOff = far;
                _farHidden[i] = far;
                _farHiddenCount += far ? 1 : -1;
            }
        }

        /// <summary>
        /// Hide only what is both far (nearest point of its bounds beyond the set distance) and small on screen
        /// (its size seen from there under the set angle). A big pipe or pump stays drawn well past the distance;
        /// bolts and lamps do not.
        /// </summary>
        private static bool IsFar(Vector3 p, Vector3 c, Vector3 e, float limitSquared, float tanLimit,
                                  int vertices, float densityLimit, float pixelScale)
        {
            var dx = Mathf.Max(Mathf.Abs(p.x - c.x) - e.x, 0f);
            var dy = Mathf.Max(Mathf.Abs(p.y - c.y) - e.y, 0f);
            var dz = Mathf.Max(Mathf.Abs(p.z - c.z) - e.z, 0f);
            var d2 = dx * dx + dy * dy + dz * dz;
            var sizeSquared = 4f * e.sqrMagnitude;
            if (d2 > limitSquared && sizeSquared < d2 * tanLimit * tanLimit)
            {
                return true;
            }
            // Too much geometry for the pixels it covers (vertices per pixel above the current limit).
            if (densityLimit < float.MaxValue && vertices >= DensityMinVertices && d2 > DensityMinDistanceSquared)
            {
                var pixels = pixelScale * sizeSquared / d2;
                return vertices > densityLimit * pixels;
            }
            return false;
        }

        private void UpdateDensityLimit()
        {
            var target = Plugin.FarObjectTargetGpuMs.Value;
            if (target <= 0f)
            {
                _densityLimit = float.MaxValue;
                return;
            }
            if (Time.unscaledTime < _nextDensityStep)
            {
                return;
            }
            _nextDensityStep = Time.unscaledTime + 0.25f;
            var timing = new Compositor_FrameTiming { m_nSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Compositor_FrameTiming)) };
            if (!OpenVR.Compositor.GetFrameTiming(ref timing, 0))
            {
                return;
            }
            var gpu = timing.m_flTotalRenderGpuMs;
            _gpuAverage = _gpuAverage < 0f ? gpu : _gpuAverage + 0.4f * (gpu - _gpuAverage);
            if (_gpuAverage > target)
            {
                _densityLimit = _densityLimit >= float.MaxValue ? 200f : Mathf.Max(_densityLimit * 0.8f, 1f);
            }
            else if (_gpuAverage < target * 0.75f && _densityLimit < float.MaxValue)
            {
                _densityLimit *= 1.15f;
                if (_densityLimit > 400f)
                {
                    _densityLimit = float.MaxValue;
                }
            }
        }

        private IEnumerator ScanFarCandidates()
        {
            var maxSize = Plugin.FarSmallObjectSize.Value;
            var all = FindObjectsOfType<MeshRenderer>();
            var renderers = new List<Renderer>();
            var centers = new List<Vector3>();
            var extents = new List<Vector3>();
            var verts = new List<int>();
            for (var i = 0; i < all.Length; i++)
            {
                var renderer = all[i];
                if (renderer != null && renderer.gameObject.layer != _mirrorLayer)
                {
                    var bounds = renderer.bounds;
                    if (bounds.size.magnitude < maxSize)
                    {
                        renderers.Add(renderer);
                        centers.Add(bounds.center);
                        extents.Add(bounds.extents);
                        verts.Add(renderer.TryGetComponent<MeshFilter>(out var filter) && filter.sharedMesh != null ? filter.sharedMesh.vertexCount : 0);
                    }
                }
                if (i % 2000 == 1999)
                {
                    yield return null; // spread the work over several frames
                }
            }

            RestoreFarSmallObjects();
            _farCandidates = renderers.ToArray();
            _farCenters = centers.ToArray();
            _farExtents = extents.ToArray();
            _farVerts = verts.ToArray();
            _farHidden = new bool[_farCandidates.Length];
            _farCursor = 0;
            Plugin.Logger.LogInfo($"Far small objects: {_farCandidates.Length:N0} of {all.Length:N0} renderers are under {maxSize:F0} m across; " +
                                  $"those more than {Plugin.FarSmallObjectDistance.Value:F0} m away are not drawn in the headset.");
            _farScan = null;
        }

        private void RestoreFarSmallObjects()
        {
            for (var i = 0; i < _farCandidates.Length; i++)
            {
                if (_farHidden[i] && _farCandidates[i] != null)
                {
                    _farCandidates[i].forceRenderingOff = false;
                }
            }
            _farCandidates = new Renderer[0];
            _farCenters = new Vector3[0];
            _farExtents = new Vector3[0];
            _farVerts = new int[0];
            _farHidden = new bool[0];
            _farHiddenCount = 0;
        }

        private void StopFarSmallObjects()
        {
            _farScan = null; // the coroutine itself is stopped with the others at shutdown
            RestoreFarSmallObjects();
        }
    }
}
