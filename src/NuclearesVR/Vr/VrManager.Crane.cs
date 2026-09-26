using HarmonyLib;
using UnityEngine;

namespace NuclearesVR.Vr
{
    /// <summary>
    /// The crane's outside camera views. Pressing the camera button in the crane seat makes the game
    /// swap its own view to a separate crane camera, which the headset never shows: the headset follows
    /// the player's head in the seat. While an outside view is selected, the headset is placed at that
    /// camera instead, keeping head rotation (but not head position, which would fight the camera).
    /// View 0 is the cab itself, so normal head tracking is used there.
    /// </summary>
    internal partial class VrManager
    {
        private static readonly System.Reflection.FieldInfo CraneCameraField = AccessTools.Field(typeof(controlCrane), "Camara");
        private static readonly System.Reflection.FieldInfo CraneViewField = AccessTools.Field(typeof(controlCrane), "_posActual");

        private Vector3 _headDeltaPos;
        private Transform _craneViewSource;
        private Quaternion _craneViewHeadRotation = Quaternion.identity;

        /// <summary>
        /// Just before an eye renders, put the rig on the crane camera again. The crane and its camera can
        /// still move after our LateUpdate (physics smoothing, other late scripts), and an old position
        /// made the crane appear in several places at once while it moved.
        /// </summary>
        private void OnEyePreCull(Camera camera)
        {
            if (_craneViewSource == null || _mainCamera == null || (camera != _leftEyeCamera && camera != _rightEyeCamera))
            {
                return;
            }
            _mainCamera.transform.SetPositionAndRotation(_craneViewSource.position, _craneViewSource.rotation * _craneViewHeadRotation);
        }

        /// <summary>The transform of the crane camera to sit at, or null when the normal view applies.</summary>
        private static Transform CraneOutsideCamera()
        {
            try
            {
                var crane = controlCrane.Instancia;
                if (crane == null || !InCrane || CraneCameraField == null || CraneViewField == null)
                {
                    return null;
                }
                var camera = CraneCameraField.GetValue(crane) as Camera;
                if (camera == null || !camera.enabled || (int)CraneViewField.GetValue(crane) == 0)
                {
                    return null;
                }
                return camera.transform;
            }
            catch
            {
                return null;
            }
        }
    }
}
