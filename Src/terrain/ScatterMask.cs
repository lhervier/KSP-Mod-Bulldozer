using System;
using UnityEngine;

namespace com.github.lhervier.ksp.bulldozermod.terrain
{
    /// <summary>
    /// Builds the mask textures handed to Parallax's exclusion decal, and computes the decal footprint
    /// that goes with them.
    ///
    /// Mask convention (read in TerrainScatters.compute): the RED channel is sampled at the candidate
    /// position and a value &lt; 0.5 blocks the scatter. So white = keep, black = clear.
    /// </summary>
    public static class ScatterMask
    {
        // Mask resolution. 1024 over a ~650 m footprint is sub-meter per pixel, plenty for a 20 m disk.
        // RGBA32 costs 4 MB per body; TextureFormat.R8 would cut that to 1 MB if it ever matters (the
        // shader only reads .r), but RGBA32 is the safest bet for a first run.
        private const int MaskSize = 1024;

        /// <summary>
        /// Footprint radius (m) to give the decal on this sphere. This is NOT the cleared radius.
        ///
        /// The decal only attaches to a quad when the angle between the decal and the quad's CENTER is below
        /// atan(radius / sphereRadius) * 4. A quad is hundreds of meters wide, so a footprint sized like the
        /// cleared area (say 20 m) would attach to no quad at all and silently do nothing. The footprint must
        /// therefore be scaled to the quad, not to the request.
        /// </summary>
        public static double FootprintRadius(PQS sphere, double clearRadius)
        {
            // A PQS face spans 90 deg and is split into 2^maxLevel quads per side.
            double quadEdge = (Math.PI * 0.5 * sphere.radius) / Math.Pow(2.0, sphere.maxLevel);
            double quadHalfDiagonal = quadEdge * 0.70710678;

            // Attachment needs footprint > quadHalfDiagonal / 4; half the half-diagonal keeps a x2 margin
            // while staying small enough for a sharp mask. The clearRadius term only matters for absurdly
            // large requests, where the cleared disk must still fit inside the footprint.
            return Math.Max(quadHalfDiagonal * 0.5, clearRadius * 4.0);
        }

        /// <summary>
        /// Creates a mask clearing a filled disk of <paramref name="clearRadius"/> meters at the center of a
        /// footprint of <paramref name="footprintRadius"/> meters.
        /// </summary>
        public static Texture2D CreateDiskMask(double footprintRadius, double clearRadius)
        {
            Texture2D mask = new Texture2D(MaskSize, MaskSize, TextureFormat.RGBA32, false);
            mask.name = "BulldozerScatterMask";

            // Bilinear + clamp mirrors how the compute shader samples it (linearClampSampler): clamping keeps
            // the "keep scatters" border value valid for anything sampled outside the mask.
            mask.filterMode = FilterMode.Bilinear;
            mask.wrapMode = TextureWrapMode.Clamp;

            // The decal's UV space spans [-footprintRadius, +footprintRadius] on both axes, centered.
            double pixelsPerMeter = MaskSize / (2.0 * footprintRadius);
            double radiusPx = clearRadius * pixelsPerMeter;
            double centerPx = MaskSize * 0.5;
            double radiusPxSq = radiusPx * radiusPx;

            Color32 keep = new Color32(255, 255, 255, 255);
            Color32 clear = new Color32(0, 0, 0, 255);

            Color32[] pixels = new Color32[MaskSize * MaskSize];
            for (int y = 0; y < MaskSize; y++)
            {
                double dy = (y + 0.5) - centerPx;
                int row = y * MaskSize;
                for (int x = 0; x < MaskSize; x++)
                {
                    double dx = (x + 0.5) - centerPx;
                    pixels[row + x] = (dx * dx + dy * dy) <= radiusPxSq ? clear : keep;
                }
            }

            mask.SetPixels32(pixels);
            mask.Apply(false, false);
            return mask;
        }
    }
}
