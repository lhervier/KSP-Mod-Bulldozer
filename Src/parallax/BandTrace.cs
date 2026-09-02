using System.Collections.Generic;
using UnityEngine;
using com.github.lhervier.ksp.shared;
using com.github.lhervier.ksp.bulldozermod.terrain;
using com.github.lhervier.ksp.bulldozermod.utils;

namespace com.github.lhervier.ksp.bulldozermod.parallax
{
    /// <summary>
    /// Paints, on the ground, the patch of surface a blade pass would clear. Drawn alongside
    /// <see cref="ScatterHighlighter"/> and switched by the same PAW entry.
    ///
    /// What it draws is the MASK CELLS, not the rectangle the player dialled in — the same cells
    /// <see cref="PassCost"/> charges for and <see cref="ScatterMask"/> will blacken. That is the whole
    /// point of drawing it at all: at the narrowest width the strip sits at the resolution limit of the
    /// mask, so the cleared corridor widens near 45 deg, steps at a quad seam, and can sit half a cell off
    /// the blade's axis. A rectangle would show none of that, and would promise ground that no decal will
    /// ever govern — on a quad another mod already owns, or beyond the quads Parallax has registered.
    ///
    /// It answers the question the rock highlight structurally cannot: the highlight can only show scatters
    /// Parallax has LOADED, so on a 200 m pass the far end shows nothing whether it is clean, out of reach,
    /// or simply empty. The trace reaches as far as the quads do.
    ///
    /// A cell is drawn by exactly one quad, the one that owns it (<see cref="QuadLocator.Contains"/>).
    /// Masks overlap by design, so drawing every mask's copy would double the alpha along every seam.
    ///
    /// Cells falling on a quad another mod's decal already owns are drawn in a SECOND colour rather than
    /// left out. The PAW already says a reservation is in the way; what it cannot say is WHERE it starts,
    /// and that is what decides how far the rover has to be moved — a 200 m pass spans four quads or more.
    ///
    /// The geometry itself belongs to <see cref="GroundSheet"/>, shared with the cleaned-zone overlay: what
    /// is proper to this class is WHICH cells to draw, and when to measure them again.
    /// </summary>
    public class BandTrace
    {
        private static readonly ModLogger LOGGER = new ModLogger("BandTrace");

        // A pass is at most 200 x 20 m, i.e. ~200 cells, so this is a guard against a future setting or a
        // bad projection, not an operating point.
        private const int MaxCells = 4096;

        // The trace is rebuilt from raycasts, so it is not free; it is also useless to rebuild it while
        // nothing moves. Both apply: at most one rebuild per interval, and only when the strip has actually
        // shifted - plus a slow refresh so a quad rebuilt underneath us eventually shows.
        //
        // The interval is the only real lever on that cost: a rebuild probes every cell corner, and each
        // corner costs one GetSurfaceHeight - which replays the whole PQSMod chain of the body, the most
        // expensive call of the PQS API - plus up to four raycasts. At 0.1 s a moving rover paid that ten
        // times a second; 0.25 s divides the bill by 2.5 for a preview that still reads as live.
        private const float RebuildInterval = 0.25f;
        private const float MaxAge = 2f;
        private const double MoveTolerance = 0.5;

        // cos(0.5 deg): the strip has turned enough to be worth redrawing.
        private const double TurnTolerance = 0.999962;

        // Lighter than the rock highlight: this covers ground the player is trying to read, and it is drawn
        // over a whole strip rather than on scattered rocks.
        private static readonly Color TraceColor = new Color(1f, 0.15f, 0.1f, 0.35f);

        // The stretch of strip lying on a quad another mod has reserved: nothing will be cleared there.
        // Amber, and more opaque than the trace, so it reads as "this part is dead" rather than "this part
        // is slightly redder".
        //
        // Amber rather than the two obvious candidates, and the reason is the shader: the one we land on is
        // LIT, so everything dims with the ground it lies on. A GREY or dark overlay is then worst exactly
        // where it is tested — grey regolith on the Mun — and vanishes on the night side; a BRIGHTER RED is
        // told apart from the trace by intensity alone, which is what dimming takes away first. A hue this
        // far from red survives both, and is absent from Mun grey, Kerbin green and Eve purple alike.
        //
        // Never yet seen in flight (nor has the trace itself): if amber does not read on some body, this is
        // the one line to change.
        private static readonly Color BlockedColor = new Color(1f, 0.78f, 0f, 0.45f);

        // Static, and never destroyed: same reasoning as the highlight's material.
        private static Material _material;
        private static Material _blockedMaterial;

        // Two sheets because a sheet is drawn through ONE material, and the shader in use carries no vertex
        // colour. They cost next to nothing side by side: their buffers grow on demand, a quad goes into
        // exactly one of them, and corner raycasts are shared within a quad — so the pair measures the very
        // same points, and the same number of them, that a single sheet measures when nothing is reserved.
        private readonly GroundSheet _sheet = new GroundSheet(MaxCells);
        private readonly GroundSheet _blockedSheet = new GroundSheet(MaxCells);

        private CelestialBody _body;
        private SurfaceBand _band;
        private bool _built;
        private float _builtAt;
        private bool _warnedCap;

        /// <summary>
        /// Draws the trace for the strip a click would apply right now, rebuilding it if it has moved.
        /// Does nothing, quietly, when there is nothing to draw.
        /// </summary>
        public void Draw(CelestialBody body, SurfaceBand band, Camera camera)
        {
            if (body == null || body.pqsController == null || camera == null)
            {
                return;
            }

            Material material = TraceMaterial();
            if (material == null)
            {
                return;
            }

            if (NeedsRebuild(body, band))
            {
                Rebuild(body, band);
            }
            _sheet.Draw(camera, material);
            _blockedSheet.Draw(camera, BlockedMaterial());
        }

        /// <summary>Releases the mesh. The material is shared and deliberately outlives the scene.</summary>
        public void Dispose()
        {
            _sheet.Dispose();
            _blockedSheet.Dispose();
            _built = false;
        }

        // The strip has to have really moved: the vessel jitters on its suspension even parked, so
        // comparing the bands exactly would rebuild every frame for nothing.
        private bool NeedsRebuild(CelestialBody body, SurfaceBand band)
        {
            if (!_built || _body != body)
            {
                return true;
            }
            if (Time.time - _builtAt < RebuildInterval)
            {
                return false;
            }
            if (Time.time - _builtAt > MaxAge)
            {
                return true;
            }
            if (band.length != _band.length || band.width != _band.width)
            {
                return true;
            }
            if (MathUtils.AngleBetween(band.origin, _band.origin) * body.Radius > MoveTolerance)
            {
                return true;
            }
            return Vector3d.Dot(band.forward, _band.forward) < TurnTolerance;
        }

        private void Rebuild(CelestialBody body, SurfaceBand band)
        {
            _body = body;
            _band = band;
            _builtAt = Time.time;
            _built = true;

            // The anchor only keeps the vertices small; the sheet follows the ground raycast by raycast.
            double surface = body.pqsController.GetSurfaceHeight(band.origin) - body.Radius;
            // Both anchored on the same point, so the two meshes are measured and drawn in one frame.
            Vector3d anchor = band.Middle * (body.Radius + surface);
            _sheet.Begin(body, anchor);
            _blockedSheet.Begin(body, anchor);

            List<QuadRef> quads = QuadLocator.FindImpactedQuads(body, band);
            for (int i = 0; i < quads.Count; i++)
            {
                // A quad another mod's decal already owns can never take ours: nothing would be cleared
                // there. Painted in its own colour rather than skipped, because a gap in the strip is
                // indistinguishable from a bug — and near the KSC (ParallaxMaskKSC) that gap is the answer.
                AddQuad(body, band, quads[i],
                        quads[i].ForeignOwner != null ? _blockedSheet : _sheet);
            }

            _sheet.End();
            _blockedSheet.End();
        }

        // Every cell of ONE quad's mask that the strip clears and that this quad owns, into the sheet that
        // carries the colour this quad is to be drawn in.
        private void AddQuad(CelestialBody body, SurfaceBand band, QuadRef quad, GroundSheet sheet)
        {
            BandRaster raster = ScatterCleaner.RasterFor(body, quad.direction, quad.halfDiagonal, band);

            int minX, minY, maxX, maxY;
            if (!raster.TryGetCellBounds(out minX, out minY, out maxX, out maxY))
            {
                return;
            }
            sheet.BeginQuad(raster.Projection, minX, minY, maxX, maxY);

            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    if (!raster.CellCleared(x, y))
                    {
                        continue;
                    }
                    if (!QuadLocator.Contains(quad.edgePlanes, raster.DirectionOf(x, y)))
                    {
                        continue;
                    }
                    if (!sheet.AddCell(x, y))
                    {
                        if (!_warnedCap)
                        {
                            LOGGER.LogWarning($"Ground trace capped at {MaxCells} cells - drawing a partial "
                                              + "strip");
                            _warnedCap = true;
                        }
                        return;
                    }
                }
            }
        }

        private static Material TraceMaterial()
        {
            if (_material == null)
            {
                _material = OverlayMaterial.Create(TraceColor, "Ground trace");
            }
            return _material;
        }

        private static Material BlockedMaterial()
        {
            if (_blockedMaterial == null)
            {
                _blockedMaterial = OverlayMaterial.Create(BlockedColor, "Ground trace (reserved)");
            }
            return _blockedMaterial;
        }
    }
}
