using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using com.github.lhervier.ksp.bulldozermod.terrain;

namespace com.github.lhervier.ksp.bulldozermod.parallax
{
    /// <summary>
    /// A sheet of mask cells laid on the terrain: the geometry every ground overlay of this mod is made of.
    ///
    /// It draws CELLS, never a shape the player dialled in, and that is the whole reason it exists — the
    /// cell is the unit Parallax's block map decides with, so it is the only unit that can honestly show
    /// where a scatter stands or falls. WHICH cells to add is the caller's business (a strip being
    /// previewed, a mask read back); measuring them, meshing them and putting them on screen is this
    /// class's, so two overlays cannot end up drawing the same ground differently.
    ///
    /// Fill it as Begin -> (BeginQuad, AddCell...)* -> End, then Draw it every frame. The mesh is stored in
    /// BODY frame relative to an anchor, so it survives the floating origin and the body's rotation without
    /// being rebuilt: only the matrix handed to DrawMesh changes.
    /// </summary>
    public class GroundSheet
    {
        // Local Scenery: the layer the flight camera draws the terrain on, and where Parallax puts the
        // scatter colliders the probes have to step past.
        private const int DrawLayer = 15;

        // How far the sheet is pulled towards the camera, per metre of distance to it, to keep it clear of
        // the terrain's own depth. Proportional on purpose: depth precision degrades with distance, so a
        // fixed offset that works up close stops working further out, while this one stays constant on
        // screen.
        private const float DepthBiasPerMetre = 0.002f;
        private const float MinDepthBias = 0.02f;

        // How far the sheet floats above the ground it was measured on, along the local vertical.
        //
        // The camera-ward nudge alone does not cover this one: at a grazing angle it is almost parallel to
        // the ground and stops separating anything. And the vertices are positioned in a frame whose origin
        // is the body center, so at planetary scale float carries them to within a few centimeters of where
        // KSP puts the terrain — the lift has to clear that too, which is why it is decimetric rather than
        // millimetric.
        private const float GroundLift = 0.15f;

        // The probe drops from this far above the ANALYTIC height of the very point it measures, over this
        // length. Taken per corner rather than once per sheet: a sheet can span a whole quad — a kilometre
        // on Kerbin — over which the ground moves far more than any single start altitude would cover, and
        // a probe that starts underground silently falls back on the analytic height. Per corner it only
        // has to cover the gap between the analytic surface and the collider, which is the sag of a mesh
        // whose vertices are ~60 m apart.
        private const double ProbeUp = 250.0;
        private const double ProbeLength = 750.0;

        // A probe can hit a scatter's collider on its way down — Parallax puts those on layer 15 too — so it
        // is restarted just under whatever it hit until it finds a quad. Two is enough for one boulder,
        // four for a cluster; past that the analytic height takes over.
        private const int ProbeAttempts = 4;

        private readonly List<Vector3> _vertices = new List<Vector3>();
        private readonly List<Vector3> _normals = new List<Vector3>();
        private readonly List<int> _triangles = new List<int>();

        // Corner sharing within ONE quad's cell box: a cell's corner is also its neighbours', and each one
        // costs a raycast. Indices into _vertices, -1 when not measured yet.
        private int[] _corners = new int[0];
        private int _cornerMinX;
        private int _cornerMinY;
        private int _cornerWidth;

        private Mesh _mesh;
        private CelestialBody _body;
        private MaskProjection _projection;
        private int _cells;
        private readonly int _maxCells;

        // Where the mesh is anchored, in body frame. Vertices are stored relative to it so they stay small:
        // an absolute position at planetary scale would quantise to a few centimeters in float.
        private Vector3d _anchor;

        /// <summary>A sheet holding at most <paramref name="maxCells"/> cells, past which AddCell refuses.</summary>
        public GroundSheet(int maxCells)
        {
            _maxCells = maxCells;
        }

        /// <summary>Cells the sheet currently holds.</summary>
        public int CellCount
        {
            get { return _cells; }
        }

        /// <summary>Whether the sheet holds anything to draw.</summary>
        public bool HasGeometry
        {
            get { return _mesh != null && _cells > 0; }
        }

        /// <summary>Whether the cell cap is reached, i.e. the next AddCell will refuse.</summary>
        public bool IsFull
        {
            get { return _cells >= _maxCells; }
        }

        /// <summary>
        /// Starts a new sheet on <paramref name="body"/>, anchored at <paramref name="anchor"/> (a position
        /// in body frame, not a direction). Discards whatever the sheet held.
        /// </summary>
        public void Begin(CelestialBody body, Vector3d anchor)
        {
            _body = body;
            _anchor = anchor;
            _cells = 0;
            _vertices.Clear();
            _normals.Clear();
            _triangles.Clear();
        }

        /// <summary>
        /// Switches to the mask grid of one quad, over the cell box the caller is about to walk. Corners are
        /// shared within that box only, which is what keeps the probe count near one per cell.
        /// </summary>
        public void BeginQuad(MaskProjection projection, int minX, int minY, int maxX, int maxY)
        {
            _projection = projection;
            _cornerMinX = minX;
            _cornerMinY = minY;

            // One more corner than cells in each direction.
            _cornerWidth = maxX - minX + 2;
            int height = maxY - minY + 2;
            int needed = _cornerWidth * height;
            if (_corners.Length < needed)
            {
                _corners = new int[needed];
            }
            for (int i = 0; i < needed; i++)
            {
                _corners[i] = -1;
            }
        }

        /// <summary>
        /// Adds one cell of the current quad's grid, measuring its corners against the terrain. Returns
        /// false, and adds nothing, once the cell cap is reached.
        /// </summary>
        public bool AddCell(int x, int y)
        {
            if (_cells >= _maxCells)
            {
                return false;
            }

            int a = Corner(x, y);
            int b = Corner(x + 1, y);
            int c = Corner(x + 1, y + 1);
            int d = Corner(x, y + 1);
            AddFace(a, b, c, d);
            _cells++;
            return true;
        }

        /// <summary>Uploads what was added to the mesh. Nothing is drawn before this is called.</summary>
        public void End()
        {
            if (_mesh == null)
            {
                _mesh = new Mesh();
                _mesh.name = "BulldozerGroundSheet";
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

        /// <summary>Draws the sheet for this frame. Does nothing when it holds no geometry.</summary>
        public void Draw(Camera camera, Material material)
        {
            if (!HasGeometry || camera == null || material == null || _body == null)
            {
                return;
            }

            // Body frame to world, rebuilt every frame: the body turns under an inertial flight frame, and
            // the floating origin shifts the whole scene. The mesh itself never moves.
            Matrix4x4 frame = BodyToWorld(_body, _anchor);

            Vector3 center = frame.MultiplyPoint3x4(Vector3.zero);
            Vector3 toCamera = camera.transform.position - center;
            float bias = Mathf.Max(MinDepthBias, DepthBiasPerMetre * toCamera.magnitude);

            // Shadows OFF: a shadow-casting sheet lying on the ground draws a black outline of itself
            // beside itself, sliding around as the sun moves.
            Graphics.DrawMesh(_mesh, Matrix4x4.Translate(toCamera.normalized * bias) * frame,
                              material, DrawLayer, camera, 0, null, ShadowCastingMode.Off, false);
        }

        /// <summary>Releases the mesh. Materials are shared and belong to the caller.</summary>
        public void Dispose()
        {
            if (_mesh != null)
            {
                UnityEngine.Object.Destroy(_mesh);
                _mesh = null;
            }
            _cells = 0;
        }

        // Two triangles, wound so they face the sky.
        //
        // The winding is MEASURED rather than reasoned about: the mask's pixel axes come from the decal's
        // own rotation, and whether (u, v, up) ends up right- or left-handed is a property of that
        // convention, not something worth deriving here — and getting it wrong makes the whole sheet
        // invisible from above, with nothing else to see.
        private void AddFace(int a, int b, int c, int d)
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
        private int Corner(int x, int y)
        {
            int slot = (y - _cornerMinY) * _cornerWidth + (x - _cornerMinX);
            if (_corners[slot] >= 0)
            {
                return _corners[slot];
            }

            Vector3d direction = _projection.CornerDirection(x, y);
            double altitude = GroundAltitude(_body, direction);

            Vector3 up = (Vector3)direction;
            _vertices.Add((Vector3)(direction * (_body.Radius + altitude) - _anchor) + up * GroundLift);
            _normals.Add(up);

            _corners[slot] = _vertices.Count - 1;
            return _corners[slot];
        }

        /// <summary>
        /// Altitude (m) of the terrain along a surface direction, measured on the collider the player is
        /// actually driving on, and falling back on the analytic height when no probe lands.
        ///
        /// The collider is what the terrain LOOKS like: the analytic height is exact only at the quad's own
        /// vertices — 60 m apart on the Mun — and the mesh runs straight between them, so a sheet laid on
        /// the analytic surface would sink into every hollow it crosses. The analytic height is still what
        /// aims the probe, and what answers when it lands on nothing.
        /// </summary>
        private static double GroundAltitude(CelestialBody body, Vector3d direction)
        {
            double analytic = body.pqsController.GetSurfaceHeight(direction) - body.Radius;
            double probeAltitude = analytic + ProbeUp;

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

            return analytic;
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
    }
}
