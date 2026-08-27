using System;
using UnityEngine;

namespace com.github.lhervier.ksp.bulldozermod.utils
{
    /// <summary>
    /// Small geometry helpers with no dependency on KSP types. Candidates for a move to KSP-Shared if
    /// another mod ever needs them.
    /// </summary>
    public static class MathUtils
    {
        /// <summary>Angle (rad) between two unit directions, safe against rounding past 1.</summary>
        public static double AngleBetween(Vector3d a, Vector3d b)
        {
            double dot = Vector3d.Dot(a, b);
            if (dot > 1.0)
            {
                dot = 1.0;
            }
            else if (dot < -1.0)
            {
                dot = -1.0;
            }
            return Math.Acos(dot);
        }
    }
}
