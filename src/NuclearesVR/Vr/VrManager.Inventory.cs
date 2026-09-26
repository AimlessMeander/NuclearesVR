using UnityEngine;

namespace NuclearesVR.Vr
{
    /// <summary>
    /// The game's inventory ("Mochila", key I) is a ring of 3D storage crates seen by a dedicated camera,
    /// plus ordinary 2D screen buttons (choose a container, close) on the overlay layer. The headset cannot
    /// see the overlay, so while the inventory is open it gets the virtual screen (see MenuOrDialogOpen),
    /// pointed at with the controller like the pause menu. What is left to do here is the drag that spins the
    /// ring: the game reads it from the "Mouse X" axis while the left button is held, and a cursor placed by
    /// the controller produces no such movement, so sideways movement on the screen is fed in as one.
    /// Both stick clicks together open and close it (see MapStickClicks).
    /// </summary>
    internal partial class VrManager
    {
        // While the inventory is open its 3D crates are a real object in the room, at about the distance of the
        // virtual screen, and the flat screen slices through them. The flat picture (a capture of the game
        // window) still needs them, so only the headset's own cameras stop drawing the inventory camera's layers.
        private int _inventoryHiddenMask;

        private void UpdateInventoryHiddenLayers()
        {
            var mask = 0;
            try
            {
                if (Interface.IsMochilaVisile)
                {
                    var camera = controlCamaras.CamMochila;
                    if (camera != null)
                    {
                        mask = camera.cullingMask & ~((1 << 0) | (1 << 5)); // never the Default or UI layers
                    }
                }
            }
            catch
            {
                mask = 0;
            }
            _inventoryHiddenMask = mask;
        }

        private float _lastSpinPixelX;
        private bool _wasSpinning;
        private const float InventorySpinPerPixel = 1f;

        private void UpdateInventorySpin(bool dragging, float pixelX)
        {
            if (dragging && Interface.IsMochilaVisile)
            {
                VrKeys.SpinInput = _wasSpinning ? (pixelX - _lastSpinPixelX) * InventorySpinPerPixel : 0f;
                _lastSpinPixelX = pixelX;
                _wasSpinning = true;
            }
            else
            {
                VrKeys.SpinInput = 0f;
                _wasSpinning = false;
            }
        }
    }
}
