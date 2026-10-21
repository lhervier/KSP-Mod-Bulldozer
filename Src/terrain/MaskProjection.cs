using System;
using UnityEngine;

namespace com.github.lhervier.ksp.mod.bulldozer.terrain
{
    /// <summary>
    /// The pixel grid of ONE mask: how a surface direction maps to a pixel of that mask, and back.
    ///
    /// It is exactly the projection <c>TerrainScatters.compute</c> applies — rotate into the decal frame,
    /// then map [-radius, +radius] onto [0, 1] — and nothing else: it knows about a mask's frame, never
    /// about what has been drawn in it. That is what lets it serve both a strip being previewed
    /// (<see cref="BandRaster"/>, which adds the strip on top of it) and a mask that already exists and is
    /// only being read back.
    ///
    /// The covered ground is a SQUARE of 2 x radius, oriented by the decal's <c>rot</c>: both axes use the
    /// same scale, so a pixel is a square on the ground whatever the shape of the quad underneath.
    /// </summary>
    public struct MaskProjection
    {
        private Quaternion _rot;
        private double _bodyRadius;
        private double _decalRadius;
        private int _size;

        /// <summary>
        /// The grid of a mask of <paramref name="size"/> pixels covering 2 x <paramref name="decalRadius"/>
        /// meters, oriented by <paramref name="rot"/> — which must be the decal's own rotation, the very one
        /// the shader applies.
        /// </summary>
        public static MaskProjection Create(Quaternion rot, double bodyRadius, double decalRadius, int size)
        {
            MaskProjection projection = new MaskProjection();
            projection._rot = rot;
            projection._bodyRadius = bodyRadius;
            projection._decalRadius = decalRadius;
            projection._size = size;
            return projection;
        }

        /// <summary>Side of the mask, in pixels.</summary>
        public int Size
        {
            get { return _size; }
        }

        /// <summary>Ground size (m) of one pixel, the same in both directions.</summary>
        public double MetersPerPixel
        {
            get { return _size > 0 ? 2.0 * _decalRadius / _size : 0.0; }
        }

        /// <summary>
        /// Pixel coordinates of a surface direction in this mask. Mirrors TerrainScatters.compute exactly:
        /// rotate into the decal frame, then map [-radius, +radius] onto [0, 1].
        /// </summary>
        public Vector2 PixelOf(Vector3d direction)
        {
            Vector3 rotated = _rot * (Vector3)direction;
            double u = (rotated.x * _bodyRadius / _decalRadius + 1.0) * 0.5;
            double v = (rotated.z * _bodyRadius / _decalRadius + 1.0) * 0.5;
            return new Vector2((float)(u * _size), (float)(v * _size));
        }

        /// <summary>Surface direction of a cell's center — the inverse of <see cref="PixelOf"/>.</summary>
        public Vector3d DirectionOf(int x, int y)
        {
            return DirectionAt(x + 0.5, y + 0.5);
        }

        /// <summary>
        /// Surface direction of a cell's lower-left CORNER — <see cref="DirectionOf"/> taken at the cell's
        /// edge rather than its middle, so cell (x, y) is bounded by the corners (x, y) to (x+1, y+1).
        ///
        /// Only a drawn outline of the cells needs this; the mask itself only ever asks about centers.
        /// </summary>
        public Vector3d CornerDirection(int x, int y)
        {
            return DirectionAt(x, y);
        }

        // Inverse of PixelOf at an arbitrary point of the pixel grid.
        //
        // The projection Parallax uses is orthographic onto the decal's tangent plane, so it inverts by
        // putting the point back on the unit sphere and undoing the decal's rotation. Points outside the
        // inscribed disk have no direction at all; they are pinned to the horizon rather than refused,
        // which is harmless because a mask is only ever read over the quad it was sized on.
        //
        // Carried through float, like PixelOf itself: that is ~6 cm on the Mun, two orders below the 5 m
        // cell this locates.
        private Vector3d DirectionAt(double px, double py)
        {
            double scale = _decalRadius / _bodyRadius;
            double localX = (px * 2.0 / _size - 1.0) * scale;
            double localZ = (py * 2.0 / _size - 1.0) * scale;
            double squared = 1.0 - localX * localX - localZ * localZ;
            double localY = squared > 0.0 ? Math.Sqrt(squared) : 0.0;

            Vector3 direction = Quaternion.Inverse(_rot)
                                * new Vector3((float)localX, (float)localY, (float)localZ);
            return new Vector3d(direction.x, direction.y, direction.z);
        }
    }
}
