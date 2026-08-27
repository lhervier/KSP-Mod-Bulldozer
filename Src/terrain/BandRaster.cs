using UnityEngine;

namespace com.github.lhervier.ksp.bulldozermod.terrain
{
    /// <summary>
    /// A <see cref="SurfaceBand"/> projected into the pixel space of ONE mask, plus the two rules that
    /// decide what the shader will see: which cells the strip clears, and what a sample between them reads.
    ///
    /// It exists as a separate type because two places need the exact same answer: <see cref="ScatterMask"/>
    /// when it fills the texture, and the highlight when it announces which rocks a pass would remove. Any
    /// second implementation would make the preview lie precisely where it is needed most — at one cell wide,
    /// the pixelisation IS the outcome.
    ///
    /// The clearing rule is "every cell the rectangle touches, however slightly". It is what makes a strip
    /// as narrow as one cell usable at all: the natural rule (cell center inside the rectangle) perforates it at any
    /// angle off the grid — at 45 deg the cleared cells only meet at their corners, where bilinear reads
    /// exactly 0.5 and the shader's strict `&lt; 0.5` keeps the scatter.
    ///
    /// What it costs AT THE NARROWEST WIDTH (one cell), measured by replaying these very rules over every
    /// angle and sub-cell offset — a wider strip dilutes all of it in proportion:
    /// the cleared corridor is never pinched (at worst 1.08 cells across, i.e. slightly MORE than the blade),
    /// but it widens to 3.16 cells near 45 deg, and it can sit up to half a cell off the blade's axis — so
    /// up to 12% of the blade's own track can stay standing, as a sliver along one edge. That last one is
    /// why the highlight samples this same code rather than the rectangle: nothing else would show it.
    ///
    /// Build one through <see cref="ScatterCleaner.RasterFor"/> unless the mask already exists: the verdict
    /// only means something in the frame the decal will really carry.
    /// </summary>
    public struct BandRaster
    {
        // The mask's frame, kept so a surface direction can be turned into pixels of THIS mask.
        private Quaternion _rot;
        private double _bodyRadius;
        private double _decalRadius;
        private int _size;

        // The strip, in pixels of that mask.
        private Vector2 _center;
        private Vector2 _axis;
        private Vector2 _side;
        private float _halfLength;
        private float _halfWidth;

        /// <summary>
        /// Projects a strip into the pixel space of a mask of <paramref name="size"/> pixels covering
        /// 2 x <paramref name="decalRadius"/> meters, oriented by <paramref name="rot"/> — which must be the
        /// decal's own rotation, the very one the shader applies.
        /// </summary>
        public static BandRaster Project(Quaternion rot, double bodyRadius, double decalRadius, int size,
                                         SurfaceBand band)
        {
            BandRaster raster = new BandRaster();
            raster._rot = rot;
            raster._bodyRadius = bodyRadius;
            raster._decalRadius = decalRadius;
            raster._size = size;

            Vector2 start = raster.PixelOf(band.origin);
            Vector2 end = raster.PixelOf(band.End);
            raster._center = (start + end) * 0.5f;

            // Only the two ends go through the full projection; the center line between them is taken
            // straight. Over the length of a pass the gnomonic sag is under a centimeter even on Gilly.
            Vector2 span = end - start;
            float spanLength = span.magnitude;
            raster._axis = spanLength > 0f ? span / spanLength : new Vector2(1f, 0f);
            raster._side = new Vector2(-raster._axis.y, raster._axis.x);
            raster._halfLength = spanLength * 0.5f;

            // The mask keeps the same scale on both axes, so one factor converts meters to pixels whatever
            // the direction.
            float pixelsPerMeter = (float)(size / (2.0 * decalRadius));
            raster._halfWidth = (float)(band.width * 0.5) * pixelsPerMeter;
            return raster;
        }

        /// <summary>Side of the mask, in pixels.</summary>
        public int Size
        {
            get { return _size; }
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

        /// <summary>
        /// Whether the strip clears that cell: does the rectangle overlap the cell's square at all.
        ///
        /// Separating-axis test between the rectangle and the unit square. Four axes are enough for two
        /// convex boxes, and both boxes' axes must be tried — the rectangle's alone would accept a cell that
        /// merely lies in one of its slabs.
        /// </summary>
        public bool CellCleared(int x, int y)
        {
            Vector2 d = new Vector2(x + 0.5f - _center.x, y + 0.5f - _center.y);

            if (Mathf.Abs(d.x * _axis.x + d.y * _axis.y)
                > _halfLength + 0.5f * (Mathf.Abs(_axis.x) + Mathf.Abs(_axis.y)))
            {
                return false;
            }
            if (Mathf.Abs(d.x * _side.x + d.y * _side.y)
                > _halfWidth + 0.5f * (Mathf.Abs(_side.x) + Mathf.Abs(_side.y)))
            {
                return false;
            }
            if (Mathf.Abs(d.x) > 0.5f + _halfLength * Mathf.Abs(_axis.x) + _halfWidth * Mathf.Abs(_side.x))
            {
                return false;
            }
            if (Mathf.Abs(d.y) > 0.5f + _halfLength * Mathf.Abs(_axis.y) + _halfWidth * Mathf.Abs(_side.y))
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// Whether a scatter sitting at that surface direction would be blocked — the same decision the
        /// shader makes: bilinear sample of the mask, blocked below 0.5.
        ///
        /// Only meaningful for a direction inside the quad this mask belongs to; elsewhere the answer is the
        /// clamped border of a mask that does not govern that ground.
        /// </summary>
        public bool Blocks(Vector3d direction)
        {
            return Sample(direction) < 0.5f;
        }

        /// <summary>
        /// The mask value the shader would read at that surface direction: 1 keeps the scatter, 0 clears it.
        /// Exposed for diagnostics — <see cref="Blocks"/> is the verdict.
        /// </summary>
        public float Sample(Vector3d direction)
        {
            Vector2 pixel = PixelOf(direction);

            // Texel centers sit at integer + 0.5, and the mask is sampled with clamp addressing.
            float x = pixel.x - 0.5f;
            float y = pixel.y - 0.5f;
            int x0 = Mathf.FloorToInt(x);
            int y0 = Mathf.FloorToInt(y);
            float fx = x - x0;
            float fy = y - y0;

            float bottom = Mathf.Lerp(Keep(x0, y0), Keep(x0 + 1, y0), fx);
            float top = Mathf.Lerp(Keep(x0, y0 + 1), Keep(x0 + 1, y0 + 1), fx);
            return Mathf.Lerp(bottom, top, fy);
        }

        /// <summary>
        /// The cells to walk to draw the strip: the rectangle's bounding box, clipped to the mask. Returns
        /// false when the strip falls entirely outside — which is expected, since a mask only ever covers its
        /// own quad and a pass usually crosses several.
        /// </summary>
        public bool TryGetCellBounds(out int minX, out int minY, out int maxX, out int maxY)
        {
            float reachX = _halfLength * Mathf.Abs(_axis.x) + _halfWidth * Mathf.Abs(_side.x);
            float reachY = _halfLength * Mathf.Abs(_axis.y) + _halfWidth * Mathf.Abs(_side.y);

            // One cell of slack around the box: a cell is cleared as soon as the rectangle touches its
            // square, so the outermost cleared cells have their center outside the rectangle.
            minX = Mathf.Max(0, Mathf.FloorToInt(_center.x - reachX) - 1);
            maxX = Mathf.Min(_size - 1, Mathf.CeilToInt(_center.x + reachX) + 1);
            minY = Mathf.Max(0, Mathf.FloorToInt(_center.y - reachY) - 1);
            maxY = Mathf.Min(_size - 1, Mathf.CeilToInt(_center.y + reachY) + 1);
            return minX <= maxX && minY <= maxY;
        }

        /// <summary>
        /// Whether the strip clears at least one cell of this mask. A bounding box that overlaps the mask is
        /// not enough to conclude — a diagonal strip can have its box reach in while the strip itself stays
        /// out — so the cells are actually tested.
        /// </summary>
        public bool ClearsAnyCell()
        {
            int minX, minY, maxX, maxY;
            if (!TryGetCellBounds(out minX, out minY, out maxX, out maxY))
            {
                return false;
            }
            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    if (CellCleared(x, y))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        // 1 keeps the scatter, 0 clears it — the mask's own convention, with the sampler's clamp addressing.
        private float Keep(int x, int y)
        {
            int cx = x < 0 ? 0 : (x > _size - 1 ? _size - 1 : x);
            int cy = y < 0 ? 0 : (y > _size - 1 ? _size - 1 : y);
            return CellCleared(cx, cy) ? 0f : 1f;
        }
    }
}
