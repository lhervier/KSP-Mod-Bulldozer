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
    /// (see "Le repère du masque" in CLAUDE.md). It is NOT aligned with the quad, and it is not stretched
    /// onto it either: both axes use the same scale, so a pixel is always a SQUARE on the ground whatever
    /// the quad's own shape.
    ///
    /// What a stroke actually covers is decided by <see cref="BandRaster"/>, not here: the same rules have
    /// to serve the highlight, which announces the outcome before the mask exists.
    /// </summary>
    public class ScatterMask
    {
        private static readonly ModLogger LOGGER = new ModLogger("Mask");

        /// <summary>
        /// Ground size (m) of one mask pixel, on every body. The texture is sized from the footprint to hold
        /// this constant.
        ///
        /// A fixed texture size cannot: the footprint follows the quad, measured in game from 299 m on
        /// Gilly to 2016 m on Eve, so one texture size would swing the ground resolution by that same
        /// factor. Note that maxLevel differs per body (7 on Gilly, 9 on the Mun, 10 on Kerbin and Eve),
        /// which is what keeps that spread down to ~7x. See "Taille du masque" in CLAUDE.md.
        /// </summary>
        public const double MetersPerPixel = 5.0;

        // Guards, not operating points: measured in game at both ends, no stock body comes close. The
        // ceiling would take a quad edge over 6.5 km, when the largest rocky body (Eve) measures 1.3 km and
        // sizes a 403 px mask; the floor would take one under ~103 m, when the smallest (Gilly) measures
        // 190 m. Both are therefore only for Kopernicus bodies.
        private const int MinSize = 32;
        private const int MaxSize = 2048;

        /// <summary>Cell value that lets the scatter stand (the shader keeps anything >= 0.5).</summary>
        public const byte Keep = 255;

        /// <summary>Cell value that removes the scatter.</summary>
        public const byte Clear = 0;

        private readonly int _size;
        private readonly Texture2D _texture;

        /// <summary>Half-width (m) of the square the mask covers; also the decal's radius.</summary>
        public double DecalRadius { get; private set; }

        /// <summary>Side of the texture, in pixels.</summary>
        public int Size
        {
            get { return _size; }
        }

        /// <summary>The texture to hand to the decal. Kept in sync by <see cref="DrawBand"/>.</summary>
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
            // load-bearing at this resolution: with the shader's 0.5 threshold, the cleared area comes out
            // as the union of the cleared cells' squares rather than as their centers. That is what makes a
            // strip one cell wide clear a full cell width -- and what BandRaster.Blocks reproduces to
            // predict it.
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
        /// Clears the strip one blade pass covers, as far as it reaches into this mask.
        ///
        /// <paramref name="rot"/> must be the decal's own rotation — the very one the shader uses. It
        /// already encodes where the decal sits, so no separate center is needed.
        ///
        /// A pass usually crosses several quads: the part of the strip that falls outside this mask is
        /// outside its quad too, and the neighbouring quad's own mask carries it.
        /// </summary>
        public void DrawBand(Quaternion rot, double bodyRadius, SurfaceBand band)
        {
            BandRaster raster = BandRaster.Project(rot, bodyRadius, DecalRadius, _size, band);

            int minX, minY, maxX, maxY;
            if (!raster.TryGetCellBounds(out minX, out minY, out maxX, out maxY))
            {
                LOGGER.LogInfo($"band of {band.length:F0} m misses this {_size}px mask entirely");
                return;
            }

            NativeArray<byte> pixels = Pixels;
            int cleared = 0;
            for (int y = minY; y <= maxY; y++)
            {
                int row = y * _size;
                for (int x = minX; x <= maxX; x++)
                {
                    if (raster.CellCleared(x, y))
                    {
                        pixels[row + x] = Clear;
                        cleared++;
                    }
                }
            }

            Apply();

            // The pixel box is what to check first if a cleared area lands somewhere unexpected: a wrong
            // vertical convention would mirror it around the mask center.
            LOGGER.LogInfo($"band {band.length:F0} x {band.width:F0} m -> {cleared} cell(s) in " +
                           $"px [{minX}..{maxX}]x[{minY}..{maxY}] of a {_size}px mask of " +
                           $"{2.0 * DecalRadius:F0} m ({2.0 * DecalRadius / _size:F2} m/px)");
        }

        /// <summary>
        /// The cells as they stand right now: <see cref="Clear"/> where a pass has already been through,
        /// <see cref="Keep"/> elsewhere, in row order.
        ///
        /// This is what makes a second pass over cleared ground free: it is read to tell the cells a pass
        /// would newly blacken from the ones it would blacken again.
        ///
        /// A VIEW on the texture, not a copy — read it into a local and use it there, it is only guaranteed
        /// valid until the texture is next modified.
        /// </summary>
        public NativeArray<byte> Cells
        {
            get { return Pixels; }
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
        // must: Pixels needs it, and later DrawBand calls complete the same mask).
        private void Apply()
        {
            _texture.Apply(false, false);
        }
    }
}
