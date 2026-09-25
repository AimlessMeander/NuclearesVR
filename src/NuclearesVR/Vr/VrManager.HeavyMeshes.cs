using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace NuclearesVR.Vr
{
    /// <summary>
    /// The game's plant is built from very high-vertex models (some merged meshes have hundreds of
    /// thousands of vertices, several are 60,000 to 350,000 each) with no level-of-detail versions,
    /// and the headset draws every one that is in view twice. In the worst views (looking out through
    /// the roof opening over the reactor pool) tens of millions of vertices are drawn, which is what
    /// holds the frame just over budget.
    ///
    /// Renderers above a vertex count are switched off (forceRenderingOff) while their NEAREST point is
    /// farther than a set distance. Using the nearest point of the bounds, not the centre, means a big
    /// merged model you are standing beside is never hidden, so nothing near you can vanish; only
    /// compact heavy objects that are entirely far away are. They come back as you approach.
    /// </summary>
    internal partial class VrManager
    {
        private readonly List<Renderer> _heavyMeshes = new List<Renderer>();
        private readonly HashSet<Renderer> _heavyHidden = new HashSet<Renderer>();
        private Coroutine _heavyScan;
        private float _nextHeavyScan;
        private int _heavyCursor;

        private void UpdateHeavyMeshes()
        {
            var limit = Plugin.HeavyMeshDistance.Value;
            if (limit <= 0f || _leftEyeCamera == null || PlayerLook.Instancia == null)
            {
                RestoreHeavyMeshes();
                return;
            }

            if (_heavyScan == null && Time.unscaledTime >= _nextHeavyScan)
            {
                _nextHeavyScan = Time.unscaledTime + 60f; // objects load and unload with the areas
                _heavyScan = StartCoroutine(ScanHeavyMeshes());
            }
            if (_heavyMeshes.Count == 0)
            {
                return;
            }

            var position = _leftEyeCamera.transform.position;
            var limitSquared = limit * limit;
            var slice = _heavyMeshes.Count / 6 + 1; // the whole list is re-checked about every 6 frames
            for (var i = 0; i < slice; i++)
            {
                if (_heavyCursor >= _heavyMeshes.Count)
                {
                    _heavyCursor = 0;
                }
                var renderer = _heavyMeshes[_heavyCursor++];
                if (renderer == null)
                {
                    continue;
                }
                var far = renderer.bounds.SqrDistance(position) > limitSquared;
                if (far && !renderer.forceRenderingOff)
                {
                    renderer.forceRenderingOff = true;
                    _heavyHidden.Add(renderer);
                }
                else if (!far && _heavyHidden.Remove(renderer))
                {
                    renderer.forceRenderingOff = false;
                }
            }
        }

        private IEnumerator ScanHeavyMeshes()
        {
            var threshold = Plugin.HeavyMeshVertices.Value;
            var all = FindObjectsOfType<MeshRenderer>();
            var found = new List<Renderer>();
            for (var i = 0; i < all.Length; i++)
            {
                var renderer = all[i];
                if (renderer != null)
                {
                    var filter = renderer.GetComponent<MeshFilter>();
                    if (filter != null && filter.sharedMesh != null && filter.sharedMesh.vertexCount >= threshold)
                    {
                        found.Add(renderer);
                    }
                }
                if (i % 1500 == 1499)
                {
                    yield return null; // spread the work over several frames
                }
            }

            RestoreHeavyMeshes();
            _heavyMeshes.AddRange(found);
            _heavyCursor = 0;
            Plugin.Logger.LogInfo($"Heavy meshes: {found.Count} renderers with at least {threshold:N0} vertices of {all.Length:N0}; " +
                                  $"those entirely farther than {Plugin.HeavyMeshDistance.Value:F0} m are not drawn in the headset.");
            _heavyScan = null;
        }

        private void RestoreHeavyMeshes()
        {
            foreach (var renderer in _heavyHidden)
            {
                if (renderer != null)
                {
                    renderer.forceRenderingOff = false;
                }
            }
            _heavyHidden.Clear();
            _heavyMeshes.Clear();
        }

        private void StopHeavyMeshes()
        {
            _heavyScan = null; // the coroutine itself is stopped with the others at shutdown
            RestoreHeavyMeshes();
        }
    }
}
