using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using com.github.lhervier.ksp.shared;
using com.github.lhervier.ksp.bulldozermod.parallax;

namespace com.github.lhervier.ksp.bulldozermod.terrain
{
    /// <summary>
    /// What a blade pass costs, in mask cells: how many cells it would turn black that are not black
    /// already.
    ///
    /// This is what a blade's wear is spent on. Consequences, all intended:
    /// going over the same ground twice costs nothing the second time; a pass laid alongside an already
    /// cleared one only pays the cells it adds; and clearing ground that happens to carry no scatter costs
    /// full price, because what is being spent is the mask, not the rocks.
    ///
    /// Only usable in flight: it reads the masks the decals currently carry, and the quad shapes that only
    /// exist once the PQS is built. That is not a limitation in practice — the cost is charged at the click,
    /// which is the only moment it matters.
    /// </summary>
    public static class PassCost
    {
        private static readonly ModLogger LOGGER = new ModLogger("PassCost");

        /// <summary>
        /// Cells the pass would newly clear across every quad it touches.
        ///
        /// A cell is charged to the quad it BELONGS TO, not to every mask that happens to draw it. A mask
        /// covers its quad's whole footprint, and that footprint overruns the quad by design (see
        /// <see cref="ScatterCleaner.FootprintMargin"/>), so a pass near a quad corner really is drawn into
        /// two to four masks. Charging all of them would make the very same gesture cost up to four times
        /// more depending on where the player happens to be standing.
        ///
        /// Exact to within a cell per seam crossed: neighbouring quads have their own grid origins, so a
        /// cell straddling a border can end up counted twice or not at all.
        /// </summary>
        public static int Compute(CelestialBody body, SurfaceBand band, IList<QuadRef> quads)
        {
            if (body == null || quads == null)
            {
                return 0;
            }

            int cost = 0;
            for (int i = 0; i < quads.Count; i++)
            {
                cost += CostOn(body, band, quads[i]);
            }
            return cost;
        }

        // Cells the pass would newly clear in ONE quad's mask.
        private static int CostOn(CelestialBody body, SurfaceBand band, QuadRef quad)
        {
            BandRaster raster = ScatterCleaner.RasterFor(body, quad.direction, quad.halfDiagonal, band);

            int minX, minY, maxX, maxY;
            if (!raster.TryGetCellBounds(out minX, out minY, out maxX, out maxY))
            {
                return 0;
            }

            // What is already cleared here, read straight off the decal that owns the quad. The save's
            // history does not need replaying: every pass reloads the game, so what is in flight is always
            // exactly what was recorded.
            NativeArray<byte> cells = default(NativeArray<byte>);
            BulldozerScatterDecal decal = quad.owner as BulldozerScatterDecal;
            bool hasMask = decal != null && decal.mask != null;
            if (hasMask && decal.mask.Size != raster.Size)
            {
                // Cannot happen as long as the half-diagonal is derived from quadMatrix, which is
                // deterministic for a given quad: both sides run it through ScatterMask.SizeFor. Worth a
                // shout if it ever does, because the fallback silently charges cleared ground again.
                LOGGER.LogWarning($"Mask on the quad at {quad.direction} is {decal.mask.Size}px but the " +
                                  $"pass rasterises at {raster.Size}px — charging as if it were untouched");
                hasMask = false;
            }
            if (hasMask)
            {
                cells = decal.mask.Cells;
            }

            int count = 0;
            for (int y = minY; y <= maxY; y++)
            {
                int row = y * raster.Size;
                for (int x = minX; x <= maxX; x++)
                {
                    // Cheapest test first, then the lookup, and only then the unprojection: this runs over
                    // the strip's bounding box every time the player nudges a slider.
                    if (!raster.CellCleared(x, y))
                    {
                        continue;
                    }
                    if (hasMask && cells[row + x] == ScatterMask.Clear)
                    {
                        continue;
                    }
                    // Charged to the quad that OWNS this cell, and to no other: over-charging is a
                    // slightly shorter blade life, under-charging is free terraforming.
                    if (!QuadLocator.Contains(quad.edgePlanes, raster.DirectionOf(x, y)))
                    {
                        continue;
                    }
                    count++;
                }
            }
            return count;
        }
    }
}
