using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Parallax.PQS_Mods;
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
    /// </summary>
    public class BandTrace
    {
        private static readonly ModLogger LOGGER = new ModLogger("BandTrace");

        // Local Scenery, same as the rock highlight: the layer the flight camera draws the terrain on.
        private const int DrawLayer = 15;

        // Same camera-ward nudge as the rock highlight, for the same reason - see ScatterHighlighter.
        private const float DepthBiasPerMetre = 0.002f;
        private const float MinDepthBias = 0.02f;

        // How far the trace floats above the ground it was measured on, along the local vertical.
        //
        // The camera-ward nudge alone does not cover this one: at a grazing angle it is almost parallel to
        // the ground and stops separating anything. And the vertices are positioned in a frame whose origin
        // is the body center, so at planetary scale float carries them to within a few centimeters of where
        // KSP puts the terrain - the lift has to clear that too, which is why it is decimetric rather than
        // millimetric.
        private const float GroundLift = 0.15f;

        // A pass is at most 200 x 20 m, i.e. ~200 cells, so this is a guard against a future setting or a
        // bad projection, not an operating point.
        private const int MaxCells = 4096;

        // The trace is rebuilt from raycasts, so it is not free; it is also useless to rebuild it while
        // nothing moves. Both apply: at most one rebuild per interval, and only when the strip has actually
        // shifted - plus a slow refresh so a quad rebuilt underneath us eventually shows.
        private const float RebuildInterval = 0.1f;
        private const float MaxAge = 2f;
        private const double MoveTolerance = 0.5;

        // cos(0.5 deg): the strip has turned enough to be worth redrawing.
        private const double TurnTolerance = 0.999962;

        // The probe drops from this far above the surface under the vessel, over this length. Generous on
        // purpose: the far end of a 200 m pass can be well above or below the near end, and a probe that
        // starts underground silently falls back on the analytic height.
        private const double ProbeUp = 250.0;
        private const double ProbeLength = 750.0;

        // A probe can hit a scatter's collider on its way down - Parallax puts those on layer 15 too - so it
        // is restarted just under whatever it hit until it finds a quad. Two is enough for one boulder,
        // four for a cluster; past that the analytic height takes over.
        private const int ProbeAttempts = 4;

        // Lighter than the rock highlight: this covers ground the player is trying to read, and it is drawn
        // over a whole strip rather than on scattered rocks.
        private static readonly Color TraceColor = new Color(1f, 0.15f, 0.1f, 0.35f);

        // Static, and never destroyed: same reasoning as the highlight's material.
        private static Material _material;

        private readonly List<Vector3> _vertices = new List<Vector3>();
        private readonly List<Vector3> _normals = new List<Vector3>();
        private readonly List<int> _triangles = new List<int>();

        // Corner sharing within ONE quad's cell box: a cell's corner is also its neighbours', and each one
        // costs a raycast. Indices into _vertices, -1 when not measured yet.
        private int[] _corners = new int[0];

        private Mesh _mesh;
        private CelestialBody _body;
        private SurfaceBand _band;
        private bool _built;
        private float _builtAt;
        private int _cells;
        private bool _warnedCap;

        // Where the mesh is anchored, in body frame. Vertices are stored relative to it so they stay small:
        // an absolute position at planetary scale would quantise to a few centimeters in float.
        private Vector3d _anchor;

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
            if (_mesh == null || _cells == 0)
            {
                return;
            }

            // Body frame to world, rebuilt every frame: the body turns under an inertial flight frame, and
            // the floating origin shifts the whole scene. The mesh itself never moves.
            Matrix4x4 frame = BodyToWorld(body, _anchor);

            Vector3 center = frame.MultiplyPoint3x4(Vector3.zero);
            Vector3 toCamera = camera.transform.position - center;
            float bias = Mathf.Max(MinDepthBias, DepthBiasPerMetre * toCamera.magnitude);

            // Shadows OFF: a shadow-casting sheet lying on the ground draws a black outline of itself
            // beside itself, sliding around as the sun moves.
            Graphics.DrawMesh(_mesh, Matrix4x4.Translate(toCamera.normalized * bias) * frame,
                              material, DrawLayer, camera, 0, null, ShadowCastingMode.Off, false);
        }

        /// <summary>Releases the mesh. The material is shared and deliberately outlives the scene.</summary>
        public void Dispose()
        {
            if (_mesh != null)
            {
                UnityEngine.Object.Destroy(_mesh);
                _mesh = null;
            }
            _built = false;
            _cells = 0;
        }

        // The strip has to have really moved: the vessel jitters on its suspension even parked, so
        // comparing the bands exactly would rebuild every frame for nothing.
        private bool NeedsRebuild(CelestialBody body, SurfaceBand band)
        {
            if (!_built || _mesh == null || _body != body)
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
            _cells = 0;
            _vertices.Clear();
            _normals.Clear();
            _triangles.Clear();

            // One height for the whole strip, only to tell the probes where to start from. The mesh follows
            // the ground raycast by raycast, not this.
            double surface = body.pqsController.GetSurfaceHeight(band.origin) - body.Radius;
            double probeAltitude = surface + ProbeUp;
            _anchor = band.Middle * (body.Radius + surface);

            List<QuadRef> quads = QuadLocator.FindImpactedQuads(body, band);
            for (int i = 0; i < quads.Count; i++)
            {
                // A quad another mod's decal already owns can never take ours: nothing would be cleared
                // there, so nothing is painted. Silently, which is exactly why it has to show (near the KSC,
                // ParallaxMaskKSC).
                PQSMod_MapDecalVertexRemoveScatter owner = quads[i].owner;
                if (owner != null && !(owner is BulldozerScatterDecal))
                {
                    continue;
                }
                AddQuad(body, band, quads[i], probeAltitude);
            }

            if (_mesh == null)
            {
                _mesh = new Mesh();
                _mesh.name = "BulldozerBandTrace";
                _mesh.MarkDynamic();
            }
            _mesh.Clear();
            if (_cells == 0)
            {
                return;
            }
            _mesh.SetVertices(_vertices);
            _mesh.SetNormals(_normals);
            _mesh.SetTriangles(_triangles, 0);
            _mesh.RecalculateBounds();
        }

        // Every cell of ONE quad's mask that the strip clears and that this quad owns.
        private void AddQuad(CelestialBody body, SurfaceBand band, QuadRef quad, double probeAltitude)
        {
            BandRaster raster = ScatterCleaner.RasterFor(body, quad.direction, quad.halfDiagonal, band);

            int minX, minY, maxX, maxY;
            if (!raster.TryGetCellBounds(out minX, out minY, out maxX, out maxY))
            {
                return;
            }

            // One more corner than cells in each direction.
            int width = maxX - minX + 2;
            int height = maxY - minY + 2;
            if (_corners.Length < width * height)
            {
                _corners = new int[width * height];
            }
            for (int i = 0; i < width * height; i++)
            {
                _corners[i] = -1;
            }

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
                    if (_cells >= MaxCells)
                    {
                        if (!_warnedCap)
                        {
                            LOGGER.LogWarning($"Ground trace capped at {MaxCells} cells - drawing a partial "
                                              + "strip");
                            _warnedCap = true;
                        }
                        return;
                    }

                    int a = Corner(body, raster, x, y, minX, minY, width, probeAltitude);
                    int b = Corner(body, raster, x + 1, y, minX, minY, width, probeAltitude);
                    int c = Corner(body, raster, x + 1, y + 1, minX, minY, width, probeAltitude);
                    int d = Corner(body, raster, x, y + 1, minX, minY, width, probeAltitude);
                    AddCell(a, b, c, d);
                    _cells++;
                }
            }
        }

        // Two triangles, wound so they face the sky.
        //
        // The winding is MEASURED rather than reasoned about: the mask's pixel axes come from the decal's
        // own rotation, and whether (u, v, up) ends up right- or left-handed is a property of that
        // convention, not something worth deriving here - and getting it wrong makes the whole trace
        // invisible from above, with nothing else to see.
        private void AddCell(int a, int b, int c, int d)
        {
            Vector3 origin = _vertices[a];
            Vector3 up = _normals[a];
            bool flip = Vector3.Dot(Vector3.Cross(_vertices[b] - origin, _vertices[c] - origin), up) < 0f;

            if (flip)
            {
                _triangles.Add(a);
                _triangles.Add(c);
                _triangles.Add(b);
                _triangles.Add(a);
                _triangles.Add(d);
                _triangles.Add(c);
            }
            else
            {
                _triangles.Add(a);
                _triangles.Add(b);
                _triangles.Add(c);
                _triangles.Add(a);
                _triangles.Add(c);
                _triangles.Add(d);
            }
        }

        // Index of a cell corner, measuring it on first use. Shared with the neighbouring cells, which is
        // what keeps the probe count down to roughly one per cell instead of four.
        private int Corner(CelestialBody body, BandRaster raster, int x, int y, int minX, int minY,
                           int width, double probeAltitude)
        {
            int slot = (y - minY) * width + (x - minX);
            if (_corners[slot] >= 0)
            {
                return _corners[slot];
            }

            Vector3d direction = raster.CornerDirection(x, y);
            double altitude = GroundAltitude(body, direction, probeAltitude);

            Vector3 up = (Vector3)direction;
            _vertices.Add((Vector3)(direction * (body.Radius + altitude) - _anchor) + up * GroundLift);
            _normals.Add(up);

            _corners[slot] = _vertices.Count - 1;
            return _corners[slot];
        }

        /// <summary>
        /// Altitude (m) of the terrain along a surface direction, measured on the collider the player is
        /// actually driving on, and falling back on the analytic height when no probe lands.
        ///
        /// The collider is what the terrain LOOKS like: the analytic height is exact only at the quad's own
        /// vertices - 60 m apart on the Mun - and the mesh runs straight between them, so a trace laid on
        /// the analytic surface would sink into every hollow it crosses.
        /// </summary>
        private static double GroundAltitude(CelestialBody body, Vector3d direction, double probeAltitude)
        {
            Vector3 down = -(Vector3)WorldDirection(body, direction);
            Vector3 from = (Vector3)ToWorld(body, direction * (body.Radius + probeAltitude));

            float travelled = 0f;
            for (int attempt = 0; attempt < ProbeAttempts; attempt++)
            {
                RaycastHit hit;
                if (!Physics.Raycast(from, down, out hit, (float)ProbeLength - travelled, 1 << DrawLayer))
                {
                    break;
                }
                if (hit.collider.GetComponentInParent<PQ>() != null)
                {
                    return probeAltitude - (travelled + hit.distance);
                }

                // Something else on the terrain layer, a scatter's collider in practice: step just past it
                // and keep going down.
                travelled += hit.distance + 0.05f;
                from = hit.point + down * 0.05f;
            }

            return body.pqsController.GetSurfaceHeight(direction) - body.Radius;
        }

        /// <summary>
        /// Body frame to world, as the matrix that puts a vertex stored relative to <paramref name="anchor"/>
        /// back where it belongs.
        ///
        /// Built from CelestialBody.BodyFrame rather than from the PQS transform: that is the conversion
        /// CelestialBody.GetWorldSurfacePosition itself uses, so it cannot disagree with the frame
        /// PQ.positionPlanetRelative lives in. The translation is worked out in double and only then
        /// narrowed, which is what keeps planetary-scale coordinates out of float.
        /// </summary>
        private static Matrix4x4 BodyToWorld(CelestialBody body, Vector3d anchor)
        {
            Vector3 x = (Vector3)WorldDirection(body, new Vector3d(1.0, 0.0, 0.0));
            Vector3 y = (Vector3)WorldDirection(body, new Vector3d(0.0, 1.0, 0.0));
            Vector3 z = (Vector3)WorldDirection(body, new Vector3d(0.0, 0.0, 1.0));
            Vector3 origin = (Vector3)ToWorld(body, anchor);

            Matrix4x4 matrix = Matrix4x4.identity;
            matrix.SetColumn(0, new Vector4(x.x, x.y, x.z, 0f));
            matrix.SetColumn(1, new Vector4(y.x, y.y, y.z, 0f));
            matrix.SetColumn(2, new Vector4(z.x, z.y, z.z, 0f));
            matrix.SetColumn(3, new Vector4(origin.x, origin.y, origin.z, 1f));
            return matrix;
        }

        // World position of a body-frame point, the inverse of CelestialBody.GetRelSurfacePosition.
        private static Vector3d ToWorld(CelestialBody body, Vector3d bodyFramePosition)
        {
            return WorldDirection(body, bodyFramePosition) + body.position;
        }

        // World direction of a body-frame vector. The .xzy swizzles are BodyFrame's own convention, kept
        // exactly as GetWorldSurfacePosition writes them.
        private static Vector3d WorldDirection(CelestialBody body, Vector3d bodyFrameVector)
        {
            return body.BodyFrame.LocalToWorld(bodyFrameVector.xzy).xzy;
        }

        private static Material TraceMaterial()
        {
            if (_material == null)
            {
                _material = OverlayMaterial.Create(TraceColor, "Ground trace");
            }
            return _material;
        }
    }
}
