using UnityEngine;
using Valve.VR;

namespace NuclearesVR.Vr
{
    /// <summary>
    /// Conversions between OpenVR's native matrix structs and Unity's Matrix4x4 /
    /// Vector3 / Quaternion.
    /// </summary>
    internal static class VrMath
    {
        public static Matrix4x4 ToMatrix4x4(this HmdMatrix44_t src)
        {
            var m = new Matrix4x4();
            m.m00 = src.m0; m.m01 = src.m1; m.m02 = src.m2; m.m03 = src.m3;
            m.m10 = src.m4; m.m11 = src.m5; m.m12 = src.m6; m.m13 = src.m7;
            m.m20 = src.m8; m.m21 = src.m9; m.m22 = src.m10; m.m23 = src.m11;
            m.m30 = src.m12; m.m31 = src.m13; m.m32 = src.m14; m.m33 = src.m15;
            return m;
        }

        /// <summary>
        /// OpenVR's device-to-absolute-tracking pose is right-handed with local
        /// forward = -Z; Unity is left-handed with local forward = +Z. Converting
        /// a rotation matrix (not just a lone vector) between the two requires a
        /// similarity transform - flip Z on both sides of the 3x3 rotation part,
        /// R_unity = flip * R_openvr * flip - not just negating the Z component
        /// of each basis column in isolation (which is what a previous version
        /// of this method did, and is only actually correct for columns 0 and 1;
        /// for column 2 the row-flip and column-flip both apply to the same
        /// element and cancel out).
        ///
        /// Working through R_unity[i][j] = R_openvr[i][j] * sign(i) * sign(j)
        /// (sign(2) = -1, sign(0)=sign(1) = +1) for each column:
        ///   right = column 0 = (m0, m4, -m8)   - only sign(i) applies
        ///   up    = column 1 = (m1, m5, -m9)   - only sign(i) applies
        ///   "local Z" = column 2: sign(i)*sign(2) applies, i.e. negate X and Y,
        ///     keep Z: but this column also needs OpenVR's forward = -local Z
        ///     folded in first, and doing that pre-negation then this transform
        ///     gives (-m2, -m6, +m10) - i.e. the full negation of what a naive
        ///     "just flip the Z component" attempt gives.
        /// Position is a plain world-space vector (not a matrix), so it only
        /// gets the simple single-component Z flip: (m3, m7, -m11).
        /// </summary>
        public static void ToUnity(this HmdMatrix34_t src, out Vector3 position, out Quaternion rotation)
        {
            position = new Vector3(src.m3, src.m7, -src.m11);

            var forward = new Vector3(-src.m2, -src.m6, src.m10);
            var up = new Vector3(src.m1, src.m5, -src.m9);
            rotation = Quaternion.LookRotation(forward, up);
        }
    }
}
