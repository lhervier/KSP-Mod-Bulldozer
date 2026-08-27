using System;
using Unity.Collections;
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
    /// (see "Le repère du masque" in the README). It is NOT aligned with the quad, and it is not stretched
    /// onto it either: both axes use the same scale, so a pixel is always a SQUARE on the ground whatever
    /// the quad's own shape.
    /// </summary>
    public class ScatterMask
    {
        private static readonly ModLogger LOGGER = new ModLogger("Mask");

        /// <summary>
        /// Ground size (m) of one mask pixel, on every body. The texture is sized from the footprint to hold
        /// this constant.
        ///
        /// A fixed texture size cannot: the footprint follows the quad, which ranges from ~80 m on Gilly to
        /// ~4.3 km on Eve, so 2048 px meant 4 cm/pixel on Gilly (8 MB for nothing) and 2 m/pixel on Eve
        /// (where a minimum-radius disk covered 5 pixels). See "Taille du masque" in the README.
        /// </summary>
        public const double MetersPerPixel = 5.0;

        // Guards, not operating points: no stock body reaches the ceiling (it would take a quad edge over
        // 6.5 km), and the floor only avoids degenerate textures on very small bodies.
        private const int MinSize = 32;
        private const int MaxSize = 2048;

        private const byte Keep = 255;
        private const byte Clear = 0;

        private readonly int _size;
        private readonly Texture2D _texture;

        /// <summary>Half-width (m) of the square the mask covers; also the decal's radius.</summary>
        public double DecalRadius { get; private set; }

        /// <summary>Side of the texture, in pixels.</summary>
        public int Size
        {
            get { return _size; }
        }

        /// <summary>The texture to hand to the decal. Kept in sync by <see cref="DrawDisk"/>.</summary>
        public Texture2D Texture
        {
            get { return _texture; }
        }

        /// <summary>
        /// Texture side (px) for a decal of that radius, so that one pixel stays
        /// <see cref="MetersPerPixel"/> across.
        ///
        /// Deliberately NOT rounded to a power of two: rounding 851 up to 1024 on Eve would give 4.15 m
        /// cells instead of 5, by a different amount on every body. R8 + bilinear + clamp has no use for a
        /// power of two, and the cell size is meant to be a stable, body-independent quantity.
        /// </summary>
        public static int SizeFor(double decalRadius)
        {
            int size = (int)Math.Round(2.0 * decalRadius / MetersPerPixel);
            if (size < MinSize)
            {
                size = MinSize;
            }
            else if (size > MaxSize)
            {
                size = MaxSize;
            }
            return size;
        }

        /// <summary>Creates an all-keep mask (nothing cleared yet) for a decal of the given radius.</summary>
        public ScatterMask(double decalRadius)
        {
            DecalRadius = decalRadius;
            _size = SizeFor(decalRadius);

            // R8 rather than RGBA32: the shader only reads .r, so one byte per pixel instead of four.
            _texture = new Texture2D(_size, _size, TextureFormat.R8, false);
            _texture.name = "BulldozerScatterMask";

            // Bilinear + clamp mirrors how the compute shader samples it (linearClampSampler). Bilinear is
            // load-bearing at this resolution: with the shader's 0.5 threshold it reconstructs the disk edge
            // at half a pixel, so a disk a handful of cells wide still reads as round rather than stepped.
            _texture.filterMode = FilterMode.Bilinear;
            _texture.wrapMode = TextureWrapMode.Clamp;

            NativeArray<byte> pixels = Pixels;
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = Keep;
            }
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
            // Both axes divide by the same DecalRadius and spread over the same _size, which is what makes
            // a pixel square and lets the disk be rasterised with a single scalar radius below.
            Vector3 rotated = rot * (Vector3)diskDirection;
            double u = (rotated.x * bodyRadius / DecalRadius + 1.0) * 0.5;
            double v = (rotated.z * bodyRadius / DecalRadius + 1.0) * 0.5;

            // Over a quad's width the gnomonic distortion is negligible, so the disk stays a disk in UV
            // space and only its center needs the full projection.
            double centerX = u * _size;
            double centerY = v * _size;
            double radiusPx = radius / (2.0 * DecalRadius) * _size;
            double radiusPxSq = radiusPx * radiusPx;

            // Clamping is expected near a quad corner on a small body: the part of the disk that falls
            // outside the mask is outside the quad too, and the neighbouring quad's own mask carries it.
            int minX = Math.Max(0, (int)Math.Floor(centerX - radiusPx));
            int maxX = Math.Min(_size - 1, (int)Math.Ceiling(centerX + radiusPx));
            int minY = Math.Max(0, (int)Math.Floor(centerY - radiusPx));
            int maxY = Math.Min(_size - 1, (int)Math.Ceiling(centerY + radiusPx));

            NativeArray<byte> pixels = Pixels;
            for (int y = minY; y <= maxY; y++)
            {
                double dy = (y + 0.5) - centerY;
                int row = y * _size;
                for (int x = minX; x <= maxX; x++)
                {
                    double dx = (x + 0.5) - centerX;
                    if (dx * dx + dy * dy <= radiusPxSq)
                    {
                        pixels[row + x] = Clear;
                    }
                }
            }

            Apply();

            // u/v and the pixel center are what to check first if a cleared area lands somewhere unexpected:
            // a wrong vertical convention would mirror it around the mask center.
            LOGGER.LogInfo($"disk r={radius:F0} m -> uv=({u:F4},{v:F4}) px=({centerX:F0},{centerY:F0}) " +
                           $"rpx={radiusPx:F1} in a {_size}px mask of {2.0 * DecalRadius:F0} m " +
                           $"({2.0 * DecalRadius / _size:F2} m/px)");
        }

        /// <summary>Frees the GPU/CPU texture. The mask must not be used afterwards.</summary>
        public void Destroy()
        {
            if (_texture != null)
            {
                UnityEngine.Object.DestroyImmediate(_texture);
            }
        }

        /// <summary>
        /// The mask pixels, one byte per pixel in row order (R8 holds exactly that).
        ///
        /// This is a VIEW on the texture's own CPU-side buffer, not a copy: GetRawTextureData wraps the
        /// existing allocation (Allocator.None), so there is no second copy of the mask to keep in sync and
        /// nothing to dispose. Re-read at each use rather than cached, because the view is only guaranteed
        /// valid until the texture is modified.
        /// </summary>
        private NativeArray<byte> Pixels
        {
            get { return _texture.GetRawTextureData<byte>(); }
        }

        // Pushes the CPU pixels written above to the GPU. No mipmaps, and the texture stays readable (it
        // must: Pixels needs it, and later DrawDisk calls complete the same mask).
        private void Apply()
        {
            _texture.Apply(false, false);
        }
    }
}
