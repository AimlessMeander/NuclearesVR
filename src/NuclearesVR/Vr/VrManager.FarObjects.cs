using System.Collections;
using System.Collections.Generic;
using UnityEngine;

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
            var slice = count / 6 + 1; // every object is re-checked about every 6 frames
            for (var n = 0; n < slice; n++)
            {
                if (_farCursor >= count)
                {
                    _farCursor = 0;
                }
                var i = _farCursor++;

                // Squared distance from the camera to the nearest point of the object's bounds.
                var c = _farCenters[i];
                var e = _farExtents[i];
                var dx = Mathf.Max(Mathf.Abs(p.x - c.x) - e.x, 0f);
                var dy = Mathf.Max(Mathf.Abs(p.y - c.y) - e.y, 0f);
                var dz = Mathf.Max(Mathf.Abs(p.z - c.z) - e.z, 0f);
                var far = dx * dx + dy * dy + dz * dz > limitSquared;
                if (far == _farHidden[i])
                {
                    continue;
                }
                var renderer = _farCandidates[i];
                if (renderer == null)
                {
                    continue;
                }
                renderer.forceRenderingOff = far;
                _farHidden[i] = far;
                _farHiddenCount += far ? 1 : -1;
            }
        }

        private IEnumerator ScanFarCandidates()
        {
            var maxSize = Plugin.FarSmallObjectSize.Value;
            var all = FindObjectsOfType<MeshRenderer>();
            var renderers = new List<Renderer>();
            var centers = new List<Vector3>();
            var extents = new List<Vector3>();
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
