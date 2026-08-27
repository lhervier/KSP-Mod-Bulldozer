using System;
using UnityEngine;

namespace com.github.lhervier.ksp.bulldozermod.terrain
{
    /// <summary>
    /// The rectangular strip one blade pass clears: where it starts, which way it goes, how long it runs.
    ///
    /// Everything is a unit direction in BODY FRAME — the frame of PQ.positionPlanetRelative, which is also
    /// what CelestialBody.GetRelSurfaceNVector produces. Nothing here ever goes through lat/lon: rebuilding
    /// a direction from lat/lon offsets it by the body's current rotation (measured at 52 km on the Mun).
    /// </summary>
    public struct SurfaceBand
    {
        /// <summary>
        /// Granularity (m) of the width, and its smallest usable value: one mask cell. Below that the mask
        /// cannot express the strip at all, and AT one cell the rasterisation rule stops being a detail —
        /// see <see cref="BandRaster"/>.
        /// </summary>
        public const double WidthStep = ScatterMask.MetersPerPixel;

        /// <summary>Unit direction of the point the strip starts from (the vessel).</summary>
        public Vector3d origin;

        /// <summary>Unit direction the strip runs towards, tangent to the surface at <see cref="origin"/>.</summary>
        public Vector3d forward;

        /// <summary>How far (m) the strip runs from <see cref="origin"/>.</summary>
        public double length;

        /// <summary>
        /// Width (m) of the blade, hence of the strip. A player setting.
        ///
        /// Do not judge a width by the instance count it blocks: measured in game, a 5 m pass blocks only
        /// 5-9% of the instances around it, yet clears a corridor tens of meters wide, because every blocked
        /// instance is a CLUSTER spread over 15-25 m. The count undersells the outcome.
        /// </summary>
        public double width;

        /// <summary>Radius (m) of the body the strip lies on, which turns its meters into angles.</summary>
        public double bodyRadius;

        /// <summary>
        /// Builds a strip from a start point and a heading, both in body frame. The heading is projected onto
        /// the surface and normalised, so any vector pointing the right way will do.
        ///
        /// Returns false when the heading has no usable horizontal component (it points at the sky or at the
        /// ground), or when the geometry is degenerate.
        /// </summary>
        public static bool TryCreate(double bodyRadius, Vector3d origin, Vector3d heading, double length,
                                     double width, out SurfaceBand band)
        {
            band = new SurfaceBand();
            if (bodyRadius <= 0.0 || length <= 0.0 || width <= 0.0 || origin.sqrMagnitude <= 0.0)
            {
                return false;
            }

            Vector3d up = origin.normalized;
            Vector3d tangent = heading - up * Vector3d.Dot(heading, up);
            if (tangent.magnitude <= 0.0)
            {
                return false;
            }

            band.bodyRadius = bodyRadius;
            band.origin = up;
            band.forward = tangent.normalized;
            band.length = length;
            band.width = width;
            return true;
        }

        /// <summary>Unit direction of the far end of the strip, on its center line.</summary>
        public Vector3d End
        {
            get { return Advance(length); }
        }

        /// <summary>Unit direction of the middle of the strip, on its center line.</summary>
        public Vector3d Middle
        {
            get { return Advance(length * 0.5); }
        }

        /// <summary>
        /// Radius (m) of a spherical cap centered on <see cref="Middle"/> that contains everything the strip
        /// can possibly clear. Used to shortlist quads before the exact per-cell test.
        /// </summary>
        public double CoarseRadius
        {
            get
            {
                // Two cells of slack, not one: the rasterisation clears every cell the rectangle touches, so
                // the cleared area reaches up to half a cell diagonal past the rectangle, and that cell then
                // extends another half diagonal further.
                return length * 0.5 + width * 0.5 + 2.0 * ScatterMask.MetersPerPixel;
            }
        }

        // Great-circle advance along the center line. Exact, so both ends of a long strip land where the
        // surface really is rather than on the tangent plane.
        private Vector3d Advance(double meters)
        {
            double angle = meters / bodyRadius;
            return origin * Math.Cos(angle) + forward * Math.Sin(angle);
        }
    }
}
