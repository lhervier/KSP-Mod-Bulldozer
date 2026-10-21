using System;
using UnityEngine;
using com.github.lhervier.ksp.mod.bulldozer.terrain;
using com.github.lhervier.ksp.mod.bulldozer.utils;

namespace com.github.lhervier.ksp.mod.bulldozer.diagnostics
{
    /// <summary>
    /// Sizes a leaf quad, for diagnostics only. Nothing in the cleaning path may size anything with this:
    /// what a decal needs is the half-diagonal of the quad it covers, which <see cref="QuadLocator"/>
    /// measures per quad at capture time.
    /// </summary>
    public static class QuadMetrics
    {
        /// <summary>Measured shape of one quad. Lengths in meters.</summary>
        public struct QuadShape
        {
            /// <summary>Side length along each grid axis, averaged over its opposite pair.</summary>
            public double sideA;
            public double sideB;

            /// <summary>
            /// Closest and furthest corner from the quad center. They differ as soon as the quad is a
            /// parallelogram rather than a rectangle, which is the rule away from a face's median lines.
            /// </summary>
            public double halfDiagonalMin;
            public double halfDiagonalMax;
        }

        /// <summary>
        /// Rough edge length (m) of a leaf quad, from the sphere alone. DIAGNOSTICS ONLY — never size
        /// anything with it, and never use it as a search radius.
        ///
        /// It assumes quads are spread evenly in ANGLE, but a PQS is a projected cube: they are even on the
        /// FACE PLANE. It therefore UNDER-estimates, by up to 4/pi (27%). Measured on the Mun (maxLevel 9,
        /// formula = 614 m): 781 m at the pole (center of a face, the 4/pi maximum), 741 m near the equator,
        /// 313 x 478 m at 45 deg (face border) — where the quad is not even square. An earlier version sized
        /// a search radius with it and found NO quad at all while the vessel sat on the corner of four.
        /// </summary>
        public static double NominalQuadEdge(PQS sphere)
        {
            return (Math.PI * 0.5 * sphere.radius) / Math.Pow(2.0, sphere.maxLevel);
        }

        /// <summary>
        /// Measures one quad's shape. Returns false when the quad has no usable geometry yet.
        ///
        /// A quad is neither square nor uniform across a cube face, so its size is always measured, never
        /// computed.
        /// </summary>
        public static bool TryMeasureShape(CelestialBody body, PQ quad, out QuadShape shape)
        {
            shape = new QuadShape();

            Vector3d[] corners = new Vector3d[4];
            if (!QuadLocator.TryGetCorners(quad, corners))
            {
                return false;
            }

            // Each side is averaged over its opposite pair: on a parallelogram the two differ slightly, and
            // neither one alone describes the quad.
            shape.sideA = (MathUtils.AngleBetween(corners[0], corners[1])
                           + MathUtils.AngleBetween(corners[3], corners[2]))
                          * 0.5 * body.Radius;
            shape.sideB = (MathUtils.AngleBetween(corners[1], corners[2])
                           + MathUtils.AngleBetween(corners[0], corners[3]))
                          * 0.5 * body.Radius;

            Vector3d quadCenter = quad.positionPlanetRelative;
            shape.halfDiagonalMin = double.MaxValue;
            shape.halfDiagonalMax = 0.0;
            for (int i = 0; i < 4; i++)
            {
                double d = MathUtils.AngleBetween(quadCenter, corners[i]) * body.Radius;
                if (d < shape.halfDiagonalMin)
                {
                    shape.halfDiagonalMin = d;
                }
                if (d > shape.halfDiagonalMax)
                {
                    shape.halfDiagonalMax = d;
                }
            }
            return true;
        }
    }
}
