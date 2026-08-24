using System;
using UnityEngine;
using com.github.lhervier.ksp.shared;

namespace com.github.lhervier.ksp.bulldozermod.terrain
{
    /// <summary>
    /// The mask texture handed to one exclusion decal, plus the projection that turns surface coordinates
    /// into pixels of that texture.
    ///
    /// Convention read by TerrainScatters.compute: the RED channel is sampled at the candidate position and
    /// a value &lt; 0.5 blocks the scatter. So 255 = keep the scatter, 0 = clear it.
    ///
    /// The mask covers a SQUARE of 2 x decalRadius, centered on the decal and oriented by the decal's `rot`
    /// (see "Le repère du masque" in the README). It is NOT aligned with the quad.
    /// </summary>
    public class ScatterMask
    {
        private static readonly ModLogger LOGGER = new ModLogger("Mask");

        // 2048 over a ~1.4 km footprint is ~0.7 m/pixel: a 5 m disk (our minimum) still spans ~14 px.
        // R8 rather than RGBA32 because the shader only reads .r — 4 MB per quad instead of 16.
        private const int MaskSize = 2048;

        private const byte Keep = 255;
        private const byte Clear = 0;

        private readonly byte[] _pixels;
        private readonly Texture2D _texture;

        /// <summary>Half-width (m) of the square the mask covers; also the decal's radius.</summary>
        public double DecalRadius { get; private set; }

        /// <summary>The texture to hand to the decal. Kept in sync by <see cref="DrawDisk"/>.</summary>
        public Texture2D Texture
        {
            get { return _texture; }
        }

        /// <summary>Creates an all-keep mask (nothing cleared yet) for a decal of the given radius.</summary>
        public ScatterMask(double decalRadius)
        {
            DecalRadius = decalRadius;

            _pixels = new byte[MaskSize * MaskSize];
            for (int i = 0; i < _pixels.Length; i++)
            {
                _pixels[i] = Keep;
            }

            _texture = new Texture2D(MaskSize, MaskSize, TextureFormat.R8, false);
            _texture.name = "BulldozerScatterMask";

            // Bilinear + clamp mirrors how the compute shader samples it (linearClampSampler).
            _texture.filterMode = FilterMode.Bilinear;
            _texture.wrapMode = TextureWrapMode.Clamp;
            Apply();
        }

        /// <summary>
        /// Clears a disk of <paramref name="radius"/> meters centered on the given surface direction.
        ///
        /// <paramref name="rot"/> must be the decal's own rotation — the very one the shader uses. It
        /// already encodes where the decal sits, so no separate center is needed.
        /// </summary>
        public void DrawDisk(Quaternion rot, double bodyRadius, Vector3d diskDirection, double radius)
        {
            // Same projection as the shader: rotate into the decal frame, then map [-R, +R] onto [0, 1].
            Vector3 rotated = rot * (Vector3)diskDirection;
            double u = (rotated.x * bodyRadius / DecalRadius + 1.0) * 0.5;
            double v = (rotated.z * bodyRadius / DecalRadius + 1.0) * 0.5;

            // Over ~1.4 km the gnomonic distortion is negligible, so the disk stays a disk in UV space and
            // only its center needs the full projection.
            double centerX = u * MaskSize;
            double centerY = v * MaskSize;
            double radiusPx = radius / (2.0 * DecalRadius) * MaskSize;
            double radiusPxSq = radiusPx * radiusPx;

            int minX = Math.Max(0, (int)Math.Floor(centerX - radiusPx));
            int maxX = Math.Min(MaskSize - 1, (int)Math.Ceiling(centerX + radiusPx));
            int minY = Math.Max(0, (int)Math.Floor(centerY - radiusPx));
            int maxY = Math.Min(MaskSize - 1, (int)Math.Ceiling(centerY + radiusPx));

            for (int y = minY; y <= maxY; y++)
            {
                double dy = (y + 0.5) - centerY;
                int row = y * MaskSize;
                for (int x = minX; x <= maxX; x++)
                {
                    double dx = (x + 0.5) - centerX;
                    if (dx * dx + dy * dy <= radiusPxSq)
                    {
                        _pixels[row + x] = Clear;
                    }
                }
            }

            Apply();

            // u/v and the pixel center are what to check first if a cleared area lands somewhere unexpected:
            // a wrong vertical convention would mirror it around the mask center.
            LOGGER.LogInfo($"disk r={radius:F0} m -> uv=({u:F4},{v:F4}) px=({centerX:F0},{centerY:F0}) " +
                           $"rpx={radiusPx:F1} in a {MaskSize}px mask of {2.0 * DecalRadius:F0} m");
        }

        /// <summary>Frees the GPU/CPU texture. The mask must not be used afterwards.</summary>
        public void Destroy()
        {
            if (_texture != null)
            {
                UnityEngine.Object.DestroyImmediate(_texture);
            }
        }

        // Raw upload: R8 holds one byte per pixel, in the same order as our buffer.
        private void Apply()
        {
            _texture.LoadRawTextureData(_pixels);
            _texture.Apply(false, false);
        }
    }
}
