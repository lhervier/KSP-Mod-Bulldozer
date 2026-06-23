using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using KSP.UI.Screens;
using UnityEngine;
using com.github.lhervier.ksp.shared;

namespace com.github.lhervier.ksp.bulldozermod.diagnostics
{
    /// <summary>
    /// Diagnostic-only toolbar button (SEPARATE from the flatten button, no side effects).
    ///
    /// On click it dumps, to the KSP log, everything needed to understand the relationship between the vessel
    /// pose stored in the .sfs (lat/lon/alt/rot/hgt) and the PQS terrain definition:
    ///   - the vessel geographic + world pose and the seating-relevant derived heights,
    ///   - the lowest collider point of the vessel (what KSP seats on the ground via getLowestPoint),
    ///   - the analytic terrain height (PQS.GetSurfaceHeight, what FlattenArea sets) vs. the actual triangle
    ///     mesh the ground raycast hits, for every vertex of the leaf quad under the rover and its neighbours.
    ///
    /// The whole point: KSP re-seats a landed vessel in Vessel.CheckGroundCollision() by raycasting against
    /// the quad MESH collider, then offsetting by getLowestPoint()/heightFromTerrain. The mesh only has a
    /// vertex every ~64-190 m and interpolates linearly between them, while GetSurfaceHeight is smooth — so
    /// the two can disagree. These logs let us measure that disagreement under the rover.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class BulldozerDiagnosticPlugin : MonoBehaviour
    {
        private static readonly ModLogger LOGGER = new ModLogger("Diag");

        // How many ring-of-neighbour quads (N/S/E/W of the leaf under the rover) to also dump. The leaf alone
        // is ~15x15 verts; adding the four edge neighbours shows the verts that bracket the vessel.
        private ApplicationLauncherButton _toolbarButton;

        // Cached reflection handle for the INTERNAL overload PQS.GetSurfaceHeight(Vector3d, bool). The public
        // overload passes false -> a runtime FlattenArea bails on its quadActive guard and returns the
        // original height (false negative). We must pass true. (See CLAUDE.md "Piège de diagnostic".)
        private static MethodInfo _getSurfaceHeightInternal;

        private void Start()
        {
            ModLogger.SetLogLevel(LogLevel.Debug);
            GameEvents.onGUIApplicationLauncherReady.Add(OnLauncherReady);
            LOGGER.LogInfo("Diagnostic plugin started");
        }

        private void OnDestroy()
        {
            GameEvents.onGUIApplicationLauncherReady.Remove(OnLauncherReady);
            RemoveToolbarButton();
        }

        // ==========================================================================
        // Toolbar
        // ==========================================================================

        private void OnLauncherReady()
        {
            if (_toolbarButton != null)
            {
                return;
            }
            try
            {
                // Deliberately a blank (flat grey) icon, NOT the bulldozer one: this button is a read-only
                // diagnostic dump, so it must be visually distinct from the flatten button to avoid clicking
                // the wrong one.
                _toolbarButton = ApplicationLauncher.Instance.AddModApplication(
                    OnClick,
                    Noop,
                    null, null, null, null,
                    ApplicationLauncher.AppScenes.FLIGHT,
                    BlankIcon());
            }
            catch (Exception e)
            {
                LOGGER.LogError("Error creating diagnostic button: " + e.Message);
            }
        }

        private void RemoveToolbarButton()
        {
            if (_toolbarButton == null)
            {
                return;
            }
            try
            {
                ApplicationLauncher.Instance.RemoveModApplication(_toolbarButton);
            }
            catch (Exception e)
            {
                LOGGER.LogError("Error removing diagnostic button: " + e.Message);
            }
            _toolbarButton = null;
        }

        private void OnClick()
        {
            try
            {
                DumpDiagnostics();
            }
            catch (Exception e)
            {
                LOGGER.LogError("Diagnostic dump failed: " + e);
            }
            finally
            {
                if (_toolbarButton != null)
                {
                    _toolbarButton.SetFalse(false);
                }
            }
        }

        private void Noop()
        {
        }

        // ==========================================================================
        // Diagnostics
        // ==========================================================================

        private void DumpDiagnostics()
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null || vessel.mainBody == null || vessel.mainBody.pqsController == null)
            {
                LOGGER.LogError("No active vessel / body / PQS");
                return;
            }

            CelestialBody body = vessel.mainBody;
            PQS pqs = body.pqsController;

            DumpVessel(vessel, body, pqs);
            DumpQuadsUnderVessel(vessel, body, pqs);
        }

        // --- Vessel pose + seating-relevant heights -------------------------------

        private void DumpVessel(Vessel vessel, CelestialBody body, PQS pqs)
        {
            double lat = vessel.latitude;
            double lon = vessel.longitude;
            Vector3d worldPos = vessel.GetWorldPos3D();
            Vector3d upAxis = (worldPos - body.position).normalized;

            // Analytic (smooth) terrain altitude at the vessel column, via the internal GetSurfaceHeight(true).
            double analyticAlt = PqsSurfaceAltitude(pqs, body, lat, lon);
            // Same thing through the public CelestialBody helper (sanity cross-check).
            double terrainAltApi = body.TerrainAltitude(lat, lon, true);

            // Lowest collider point of the whole vessel and of the wheels only (what getLowestPoint cares
            // about). Altitude = distance from planet centre minus body radius.
            double lowestAllAlt = LowestColliderAltitude(vessel, body, wheelsOnly: false, out Vector3d lowestAllPos);
            double lowestWheelAlt = LowestColliderAltitude(vessel, body, wheelsOnly: true, out Vector3d lowestWheelPos);

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("================= VESSEL =================");
            sb.AppendLine($"name            : {vessel.vesselName}");
            sb.AppendLine($"body            : {body.name}  Radius={body.Radius:F3}");
            sb.AppendLine($"situation       : {vessel.situation}  Landed={vessel.Landed}  Splashed={vessel.Splashed}");
            sb.AppendLine($"lat / lon       : {lat:F10} / {lon:F10}");
            sb.AppendLine($"alt (ASL)       : {vessel.altitude:F4}        (sfs 'alt')");
            sb.AppendLine($"radarAltitude   : {vessel.radarAltitude:F4}");
            sb.AppendLine($"heightFromTerr. : {vessel.heightFromTerrain:F4}     (sfs 'hgt' ~ raycast dist to mesh)");
            sb.AppendLine($"terrainAltitude : {vessel.terrainAltitude:F4}");
            sb.AppendLine($"world pos       : {V(worldPos)}");
            sb.AppendLine($"body  pos       : {V(body.position)}");
            sb.AppendLine($"up axis         : {V(upAxis)}");
            sb.AppendLine($"vesselTransform : pos={V(vessel.vesselTransform.position)} rot={Q(vessel.vesselTransform.rotation)}");
            sb.AppendLine($"srfRelRotation  : {Q(vessel.srfRelRotation)}");
            sb.AppendLine($"CoM (world)     : {V(vessel.CoM)}  alt={body.GetAltitude(vessel.CoM):F4}");
            sb.AppendLine("--- terrain under vessel column ---");
            sb.AppendLine($"PQS analytic alt: {analyticAlt:F4}     (GetSurfaceHeight(true) - Radius; what FlattenArea sets)");
            sb.AppendLine($"TerrainAltitude : {terrainAltApi:F4}     (CelestialBody.TerrainAltitude)");
            sb.AppendLine($"  -> analytic vs API delta : {(analyticAlt - terrainAltApi):F4}");
            sb.AppendLine("--- vessel lowest points (seating) ---");
            sb.AppendLine($"lowest collider : alt={lowestAllAlt:F4}  pos={V(lowestAllPos)}");
            sb.AppendLine($"lowest wheel    : alt={lowestWheelAlt:F4}  pos={V(lowestWheelPos)}  (∞ = no wheels)");
            sb.AppendLine($"  vessel alt - lowest collider alt = {(vessel.altitude - lowestAllAlt):F4}  (how far the lowest point sits below the reference)");
            sb.Append("=========================================");
            LOGGER.LogInfo(sb.ToString());
        }

        // Lowest altitude reached by any collider of the vessel (optionally restricted to wheel parts), found
        // by scanning the 8 world-AABB corners of each collider. Approximates the role of Vessel.getLowestPoint.
        private double LowestColliderAltitude(Vessel vessel, CelestialBody body, bool wheelsOnly, out Vector3d lowestPos)
        {
            double minAlt = double.PositiveInfinity;
            lowestPos = Vector3d.zero;

            foreach (Part part in vessel.parts)
            {
                if (wheelsOnly && part.GetComponent<ModuleWheelBase>() == null)
                {
                    continue;
                }
                foreach (Collider col in part.GetComponentsInChildren<Collider>())
                {
                    if (!col.enabled || col.isTrigger)
                    {
                        continue;
                    }
                    Bounds b = col.bounds;
                    for (int i = 0; i < 8; i++)
                    {
                        Vector3 corner = new Vector3(
                            (i & 1) == 0 ? b.min.x : b.max.x,
                            (i & 2) == 0 ? b.min.y : b.max.y,
                            (i & 4) == 0 ? b.min.z : b.max.z);
                        double alt = body.GetAltitude(corner);
                        if (alt < minAlt)
                        {
                            minAlt = alt;
                            lowestPos = corner;
                        }
                    }
                }
            }
            return minAlt;
        }

        // --- PQS quads (mesh) vs analytic height ----------------------------------

        private void DumpQuadsUnderVessel(Vessel vessel, CelestialBody body, PQS pqs)
        {
            Vector3 roverPos = vessel.vesselTransform.position;

            List<PQ> leaves = new List<PQ>();
            if (pqs.quads != null)
            {
                foreach (PQ root in pqs.quads)
                {
                    CollectLeaves(root, leaves);
                }
            }

            if (leaves.Count == 0)
            {
                LOGGER.LogWarning("No built leaf quads found");
                return;
            }

            // Closest leaf to the rover = the quad under it.
            PQ under = null;
            float best = float.PositiveInfinity;
            foreach (PQ q in leaves)
            {
                float d = (q.transform.position - roverPos).sqrMagnitude;
                if (d < best)
                {
                    best = d;
                    under = q;
                }
            }

            LOGGER.LogInfo($"PQS maxLevel={pqs.maxLevel}  built leaves={leaves.Count}  leaf under rover sub={under.subdivision}");

            // The leaf under the rover plus its four edge neighbours (may be null / coarser).
            List<PQ> toDump = new List<PQ> { under };
            AddIfNew(toDump, under.north);
            AddIfNew(toDump, under.south);
            AddIfNew(toDump, under.east);
            AddIfNew(toDump, under.west);

            foreach (PQ q in toDump)
            {
                DumpQuad(q, body, pqs, roverPos);
            }
        }

        private static void CollectLeaves(PQ quad, List<PQ> outList)
        {
            if (quad == null)
            {
                return;
            }
            if (quad.isSubdivided && quad.subNodes != null)
            {
                foreach (PQ child in quad.subNodes)
                {
                    CollectLeaves(child, outList);
                }
                return;
            }
            if (quad.isBuilt && quad.mesh != null)
            {
                outList.Add(quad);
            }
        }

        private static void AddIfNew(List<PQ> list, PQ q)
        {
            if (q != null && q.isBuilt && q.mesh != null && !list.Contains(q))
            {
                list.Add(q);
            }
        }

        // Dumps every mesh vertex of a quad: its world pos, lat/lon, mesh altitude (the actual triangle the
        // ground raycast hits) and the analytic GetSurfaceHeight altitude at the same direction, plus their
        // delta. A non-zero delta is exactly the mesh-vs-analytic mismatch that unseats the rover.
        private void DumpQuad(PQ quad, CelestialBody body, PQS pqs, Vector3 roverPos)
        {
            Vector3[] verts = quad.mesh.vertices;
            Transform t = quad.transform;

            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"---------- QUAD '{quad.gameObject.name}' sub={quad.subdivision} verts={verts.Length} " +
                          $"meshVert[min={quad.meshVertMin:F2} max={quad.meshVertMax:F2}] centreDistToRover={(t.position - roverPos).magnitude:F2}m ----------");
            sb.AppendLine("idx :        worldX        worldY        worldZ |        lat        lon |   meshAlt analyticAlt    delta | distRover");

            for (int i = 0; i < verts.Length; i++)
            {
                Vector3d world = t.TransformPoint(verts[i]);
                body.GetLatLonAlt(world, out double vlat, out double vlon, out double meshAlt);
                double analyticAlt = PqsSurfaceAltitude(pqs, body, vlat, vlon);
                double distRover = (world - (Vector3d)roverPos).magnitude;

                sb.AppendLine($"{i,3} : {world.x,13:F3} {world.y,13:F3} {world.z,13:F3} | " +
                              $"{vlat,10:F6} {vlon,10:F6} | {meshAlt,9:F3} {analyticAlt,9:F3} {(meshAlt - analyticAlt),8:F3} | {distRover,9:F2}");
            }
            LOGGER.LogInfo(sb.ToString());
        }

        // --- helpers --------------------------------------------------------------

        // Analytic terrain ALTITUDE (above sea level) at lat/lon, via the internal GetSurfaceHeight(...,true).
        private static double PqsSurfaceAltitude(PQS pqs, CelestialBody body, double lat, double lon)
        {
            if (_getSurfaceHeightInternal == null)
            {
                _getSurfaceHeightInternal = typeof(PQS).GetMethod(
                    "GetSurfaceHeight",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                    null,
                    new[] { typeof(Vector3d), typeof(bool) },
                    null);
            }
            Vector3d radial = body.GetRelSurfaceNVector(lat, lon);
            double surfaceRadius = (double)_getSurfaceHeightInternal.Invoke(pqs, new object[] { radial, true });
            return surfaceRadius - body.Radius;
        }

        private static string V(Vector3d v) => $"({v.x:F3}, {v.y:F3}, {v.z:F3})";
        private static string Q(Quaternion q) => $"({q.x:F5}, {q.y:F5}, {q.z:F5}, {q.w:F5})";

        // A flat grey square generated in code (no GameData asset needed) so the diagnostic button is clearly
        // "empty" next to the bulldozer flatten button.
        private static Texture2D _blankIcon;
        private static Texture2D BlankIcon()
        {
            if (_blankIcon != null)
            {
                return _blankIcon;
            }
            const int size = 38;
            _blankIcon = new Texture2D(size, size, TextureFormat.ARGB32, false);
            Color fill = new Color(0.30f, 0.30f, 0.30f, 1f);
            Color[] pixels = new Color[size * size];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = fill;
            }
            _blankIcon.SetPixels(pixels);
            _blankIcon.Apply();
            return _blankIcon;
        }
    }
}
