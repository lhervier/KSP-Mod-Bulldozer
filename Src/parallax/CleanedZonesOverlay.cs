using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using Parallax.PQS_Mods;
using com.github.lhervier.ksp.shared;
using com.github.lhervier.ksp.bulldozermod.terrain;

namespace com.github.lhervier.ksp.bulldozermod.parallax
{
    /// <summary>
    /// Paints in green, on the ground, every cell already cleared around the vessel. A debug overlay,
    /// toggled from the mod window.
    ///
    /// It reads the MASKS THEMSELVES, never the recorded actions, and that is the whole point: the two
    /// differ exactly where the bugs are. An action can be dropped at injection (its body has no PQS, the
    /// terrain detail preset changed since it was recorded), and a quad can refuse our decal because
    /// another mod's already owns it (ParallaxMaskKSC around the space center). None of that is announced
    /// on screen. Drawn from the masks, such a failure shows up as a hole in the green — which is the
    /// question this overlay exists to answer: is the ground that was paid for really the ground that is
    /// governed?
    ///
    /// A cell is painted by exactly one quad, the one that owns it (<see cref="QuadLocator.Contains"/>).
    /// A mask covers a footprint wider than its own quad, so the same ground is black in two to four of
    /// them, and painting every copy would both double the alpha and show ground that mask does not rule.
    ///
    /// What it does NOT show, deliberately: whether the rocks actually went. The mask is only consulted
    /// when Parallax builds a quad, so a green cell says "this ground is excluded", not "this ground is
    /// clear". A boulder standing on green is either a cluster anchored outside (its mesh reaches in — see
    /// the cluster trap in CLAUDE.md) or a quad that was already built when the decal was injected. Telling
    /// those two apart is exactly what the overlay is for.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class CleanedZonesOverlay : MonoBehaviour
    {
        private static readonly ModLogger LOGGER = new ModLogger("CleanedZones");

        /// <summary>
        /// Toggled from the debug window. Static because this addon is destroyed and rebuilt at every scene
        /// change, while the choice belongs to the player.
        /// </summary>
        public static bool Active;

        // Green, and lighter than the pass preview: this one covers ground the player already paid for and
        // is only checking, while the red trace is about a decision being made.
        private static readonly Color ZoneColor = new Color(0.15f, 1f, 0.2f, 0.3f);

        // Per quad. A blade's whole quota is 200 to 2000 cells, so a quad carrying more than this has been
        // worked by several blades; the cap is a guard against a runaway mask, not an operating point.
        private const int MaxCellsPerQuad = 8192;

        // Quads meshed per frame. Measuring a quad costs one raycast per cell corner, so building every
        // freshly loaded quad in the same frame would show up as a hitch while driving. The sheets are
        // cached afterwards and cost nothing but a DrawMesh.
        private const int BuildsPerFrame = 1;

        // Static, and never destroyed: same reasoning as the other overlays' materials.
        private static Material _material;

        // One sheet per quad, keyed by the quad itself: PQ instances are destroyed and recreated as the
        // terrain streams, so a new instance is a new entry and the old one is pruned. Sheets with no cell
        // are kept too — they cache the "nothing cleared here" answer, which is most quads.
        private readonly Dictionary<PQ, GroundSheet> _sheets = new Dictionary<PQ, GroundSheet>();

        // Reused every frame: which quads the registry still holds, and which cache entries to drop.
        private readonly HashSet<PQ> _live = new HashSet<PQ>();
        private readonly List<PQ> _doomed = new List<PQ>();

        // Scratch buffers, so walking the registry allocates nothing.
        private readonly Vector3d[] _corners = new Vector3d[4];
        private readonly Vector3d[] _planes = new Vector3d[4];

        private bool _warnedCap;
        private int _lastDrawn = -1;

        private void Update()
        {
            if (!Active)
            {
                Clear();
                return;
            }

            // Draw ONLY on a settled flight scene. Active is static, so it survives a scene change and we
            // would otherwise keep drawing all through a reload, while quads and their GPU buffers are being
            // torn down and rebuilt.
            if (!HighLogic.LoadedSceneIsFlight || !FlightGlobals.ready)
            {
                return;
            }

            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null || vessel.mainBody == null || vessel.packed)
            {
                return;
            }

            CelestialBody body = vessel.mainBody;
            if (body.pqsController == null)
            {
                return;
            }

            // ONE camera, never null: `camera: null` submits the draw to EVERY camera in the game, and an
            // overlay only makes sense in the one being looked at.
            Camera camera = Camera.main;
            if (camera == null)
            {
                return;
            }

            Material material = ZoneMaterial();
            if (material == null)
            {
                return;
            }

            DrawAll(body, camera, material);
        }

        private void OnDestroy()
        {
            Clear();
        }

        // Walk the quads our decals are attached to, meshing the ones not cached yet (a few per frame) and
        // drawing the rest.
        private void DrawAll(CelestialBody body, Camera camera, Material material)
        {
            _live.Clear();
            int built = 0;
            int drawn = 0;
            int cells = 0;

            foreach (KeyValuePair<PQ, PQSMod_MapDecalVertexRemoveScatter> entry
                     in PQSMod_MapDecalVertexRemoveScatter.maskedScattersPerQuad)
            {
                PQ quad = entry.Key;
                if (quad == null || quad.gameObject == null)
                {
                    continue;
                }

                // Only OUR decals describe ground we cleared. A quad held by another mod carries no mask of
                // ours, and showing its exclusion zone in our colour would claim work we never did.
                BulldozerScatterDecal decal = entry.Value as BulldozerScatterDecal;
                if (decal == null || decal.mask == null || decal.sphere != body.pqsController)
                {
                    continue;
                }
                _live.Add(quad);

                GroundSheet sheet;
                if (!_sheets.TryGetValue(quad, out sheet))
                {
                    if (built >= BuildsPerFrame)
                    {
                        // Not this frame. The quad is in _live, so nothing prunes it meanwhile.
                        continue;
                    }
                    sheet = Build(body, quad, decal);
                    _sheets[quad] = sheet;
                    built++;
                }

                if (sheet.HasGeometry)
                {
                    sheet.Draw(camera, material);
                    drawn++;
                    cells += sheet.CellCount;
                }
            }

            Prune();

            // One line per change, not per frame: it is the quick check that the overlay is looking at
            // something at all when nothing shows up on screen.
            if (drawn != _lastDrawn)
            {
                LOGGER.LogInfo($"Showing {cells} cleared cell(s) over {drawn} quad(s) "
                               + $"({_sheets.Count} quad(s) known)");
                _lastDrawn = drawn;
            }
        }

        // The sheet for one quad: every cell its own mask has blackened, minus the ones that belong to a
        // neighbour.
        private GroundSheet Build(CelestialBody body, PQ quad, BulldozerScatterDecal decal)
        {
            GroundSheet sheet = new GroundSheet(MaxCellsPerQuad);

            Vector3d quadCenter = quad.positionPlanetRelative;
            double surface = body.pqsController.GetSurfaceHeight(quadCenter) - body.Radius;
            sheet.Begin(body, quadCenter * (body.Radius + surface));

            // The decal's own rotation and radius, not a reconstruction of them: this mask exists, and it is
            // the grid the shader is really sampling.
            ScatterMask mask = decal.mask;
            MaskProjection projection = MaskProjection.Create(decal.rot, body.Radius, mask.DecalRadius,
                                                             mask.Size);

            // Read into a local: Cells is a view on the texture, valid only until it is next modified.
            NativeArray<byte> cells = mask.Cells;

            int minX, minY, maxX, maxY;
            if (!TryGetClearedBounds(cells, mask.Size, out minX, out minY, out maxX, out maxY))
            {
                sheet.End();
                return sheet;
            }

            // The quad's four bounding planes, so a cell is drawn by the one mask that governs it. A quad
            // whose matrix is not usable yet contains everything, which over-paints rather than drops
            // ground — the same choice QuadLocator.Contains makes.
            Vector3d[] edgePlanes = null;
            if (QuadLocator.TryGetCorners(quad, _corners)
                && QuadLocator.TryGetEdgePlanes(_corners, quadCenter, _planes))
            {
                edgePlanes = _planes;
            }

            sheet.BeginQuad(projection, minX, minY, maxX, maxY);
            for (int y = minY; y <= maxY; y++)
            {
                int row = y * mask.Size;
                for (int x = minX; x <= maxX; x++)
                {
                    if (cells[row + x] != ScatterMask.Clear)
                    {
                        continue;
                    }
                    if (!QuadLocator.Contains(edgePlanes, projection.DirectionOf(x, y)))
                    {
                        continue;
                    }
                    if (!sheet.AddCell(x, y))
                    {
                        if (!_warnedCap)
                        {
                            LOGGER.LogWarning($"Cleaned-zone overlay capped at {MaxCellsPerQuad} cells on a "
                                              + "quad - showing a partial zone");
                            _warnedCap = true;
                        }
                        y = maxY;
                        break;
                    }
                }
            }

            sheet.End();
            return sheet;
        }

        // Bounding box of the blackened cells. Walking the whole mask is one byte read per cell (53 kB on
        // the Mun, 158 kB on Eve, once per quad) and it is what keeps the meshing loop down to the handful
        // of cells that were actually cleared.
        private static bool TryGetClearedBounds(NativeArray<byte> cells, int size,
                                                out int minX, out int minY, out int maxX, out int maxY)
        {
            minX = size;
            minY = size;
            maxX = -1;
            maxY = -1;

            for (int y = 0; y < size; y++)
            {
                int row = y * size;
                for (int x = 0; x < size; x++)
                {
                    if (cells[row + x] != ScatterMask.Clear)
                    {
                        continue;
                    }
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
            return maxX >= 0;
        }

        // Drop the sheets of quads the registry no longer holds: the terrain streams, and a PQ that went
        // away takes its mask with it.
        private void Prune()
        {
            _doomed.Clear();
            foreach (KeyValuePair<PQ, GroundSheet> entry in _sheets)
            {
                if (!_live.Contains(entry.Key))
                {
                    _doomed.Add(entry.Key);
                }
            }
            for (int i = 0; i < _doomed.Count; i++)
            {
                _sheets[_doomed[i]].Dispose();
                _sheets.Remove(_doomed[i]);
            }
        }

        private void Clear()
        {
            _lastDrawn = -1;
            foreach (KeyValuePair<PQ, GroundSheet> entry in _sheets)
            {
                entry.Value.Dispose();
            }
            _sheets.Clear();
        }

        private static Material ZoneMaterial()
        {
            if (_material == null)
            {
                _material = OverlayMaterial.Create(ZoneColor, "Cleaned zones");
            }
            return _material;
        }
    }
}
