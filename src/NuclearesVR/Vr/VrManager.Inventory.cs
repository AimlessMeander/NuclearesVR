using System.Linq;
using UnityEngine;

namespace NuclearesVR.Vr
{
    /// <summary>
    /// The game's inventory ("Mochila", key I) is not a flat menu. Opening it switches on a dedicated camera
    /// that looks at a ring of 3D storage containers; you drag to spin the ring and click a container to open it.
    /// The headset never showed that camera, so while the inventory is open:
    ///  - the eye cameras also draw the layers that camera draws,
    ///  - the headset sits at that camera (turning with the head, like the crane's outside views),
    ///  - the controller that points also aims that camera, so the game's mouse events (clicks on the
    ///    containers) hit what the controller points at,
    ///  - hand movement while the trigger is held spins the ring (it reads the "Mouse X" axis).
    /// Both stick clicks together open and close it (see MapStickClicks).
    /// </summary>
    internal partial class VrManager
    {
        private int _inventoryMask;
        private bool _inventoryWasOpen;

        /// <summary>The inventory's camera while the inventory is open, else null.</summary>
        private static Camera InventoryCamera()
        {
            try
            {
                if (!Interface.IsMochilaVisile)
                {
                    return null;
                }
                var camera = controlCamaras.CamMochila;
                return camera != null && camera.enabled ? camera : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Once per frame, early in LateUpdate: which extra layers the eyes must draw, and a log when it opens or closes.</summary>
        private void UpdateInventoryState()
        {
            var camera = InventoryCamera();
            _inventoryMask = camera != null ? camera.cullingMask : 0;
            var open = camera != null;
            if (open == _inventoryWasOpen)
            {
                return;
            }
            _inventoryWasOpen = open;
            if (!open)
            {
                Plugin.Logger.LogInfo("[inventory] closed.");
                return;
            }
            try
            {
                var head = _mainCamera != null ? _mainCamera.transform.position : Vector3.zero;
                Plugin.Logger.LogInfo($"[inventory] opened. camera '{camera.name}' pos={camera.transform.position} rot={camera.transform.eulerAngles} " +
                                      $"({(camera.transform.position - head).magnitude:F2} m from the player camera), parent={(camera.transform.parent != null ? camera.transform.parent.name : "-")}, " +
                                      $"mask={camera.cullingMask}, depth={camera.depth}, fov={camera.fieldOfView:F0}, near={camera.nearClipPlane:F2}, far={camera.farClipPlane:F0}, clear={camera.clearFlags}");
                var containers = FindObjectsOfType<ContenedorMochila>();
                Plugin.Logger.LogInfo($"[inventory] {containers.Length} containers; nearest to the camera: " +
                                      string.Join(", ", containers.OrderBy(c => (c.transform.position - camera.transform.position).sqrMagnitude).Take(4)
                                          .Select(c => $"{c.name} at {(c.transform.position - camera.transform.position).magnitude:F2} m on layer {c.gameObject.layer}").ToArray()));
            }
            catch (System.Exception ex)
            {
                Plugin.Logger.LogWarning($"[inventory] logging failed: {ex.Message}");
            }
        }

        // ---- the controller aims the inventory camera as well, for the game's mouse events ----
        private Camera _pointedInventoryCamera;
        private Vector3 _pointedInventoryPosition;
        private Quaternion _pointedInventoryRotation;

        private void AimInventoryCamera(Vector3 position, Quaternion rotation)
        {
            var camera = InventoryCamera();
            if (camera == null)
            {
                return;
            }
            _pointedInventoryCamera = camera;
            _pointedInventoryPosition = camera.transform.position;
            _pointedInventoryRotation = camera.transform.rotation;
            camera.transform.SetPositionAndRotation(position, rotation);
        }

        private void RestoreInventoryCamera()
        {
            if (_pointedInventoryCamera == null)
            {
                return;
            }
            _pointedInventoryCamera.transform.SetPositionAndRotation(_pointedInventoryPosition, _pointedInventoryRotation);
            _pointedInventoryCamera = null;
        }

        // ---- spinning the ring: hand movement while dragging feeds the "Mouse X" axis ----
        private float _lastDragOffsetX;
        private const float InventorySpinPerPixel = 0.25f;

        private void UpdateInventorySpin(bool dragging)
        {
            if (dragging && _inventoryWasOpen)
            {
                var x = VrKeys.MouseOffset.x;
                VrKeys.SpinInput = (x - _lastDragOffsetX) * InventorySpinPerPixel;
                _lastDragOffsetX = x;
            }
            else
            {
                VrKeys.SpinInput = 0f;
                _lastDragOffsetX = 0f;
            }
        }
    }
}
