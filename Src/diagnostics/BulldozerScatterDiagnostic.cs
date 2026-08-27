using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Parallax;
using Parallax.PQS_Mods;
using com.github.lhervier.ksp.shared;
using com.github.lhervier.ksp.bulldozermod.terrain;

namespace com.github.lhervier.ksp.bulldozermod.diagnostics
{
    /// <summary>
    /// Read-only diagnostic: once the flight scene has settled, dumps to KSP.log what actually happened to
    /// our exclusion decal — whether it was set up by the PQS, whether it attached to quads, and whether the
    /// quad under the vessel reads our block map.
    ///
    /// Temporary, meant to be removed once the mechanism is proven.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class BulldozerScatterDiagnostic : MonoBehaviour
    {
        private static readonly ModLogger LOGGER = new ModLogger("Diag");

        private const string DecalGoName = "BulldozerScatterDecal";

        // Long enough for the terrain to have subdivided to max level around the vessel.
        private const float SettleDelay = 8f;

        private void Start()
        {
            StartCoroutine(DumpAfterSettle());
        }

        private IEnumerator DumpAfterSettle()
        {
            yield return new WaitForSeconds(SettleDelay);
            try
            {
                Dump();
            }
            catch (System.Exception e)
            {
                LOGGER.LogError("Diagnostic failed: " + e);
            }
        }

        private void Dump()
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null || vessel.mainBody?.pqsController == null)
            {
                LOGGER.LogInfo("No vessel / PQS — nothing to dump");
                return;
            }

            CelestialBody body = vessel.mainBody;
            PQS sphere = body.pqsController;
            PQSMod[] mods = GetSphereMods(sphere);
            LOGGER.LogInfo($"=== {body.name}: maxLevel={sphere.maxLevel} modCount={mods?.Length}");

            DumpOurDecals(sphere, mods);
            DumpParallaxState(body, vessel);
        }

        // Is our decal in the PQS mod list at all, and did OnSetup run on it? inclusionAngle stays 0 when
        // OnSetup did not run, which alone would explain a decal that attaches to no quad.
        // PQS.mods is private; it is the list SetupMods() actually built, so it is the only proof our decal
        // was collected.
        private static PQSMod[] GetSphereMods(PQS sphere)
        {
            FieldInfo field = typeof(PQS).GetField("mods", BindingFlags.Instance | BindingFlags.NonPublic);
            return field?.GetValue(sphere) as PQSMod[];
        }

        private void DumpOurDecals(PQS sphere, PQSMod[] mods)
        {
            int found = 0;
            foreach (Transform child in sphere.transform)
            {
                if (child.name != DecalGoName)
                {
                    continue;
                }
                found++;

                PQSMod_MapDecalVertexRemoveScatter decal = child.GetComponent<PQSMod_MapDecalVertexRemoveScatter>();
                if (decal == null)
                {
                    LOGGER.LogError("  decal GameObject carries no PQSMod!");
                    continue;
                }

                bool inModList = false;
                if (mods != null)
                {
                    foreach (PQSMod mod in mods)
                    {
                        if (ReferenceEquals(mod, decal))
                        {
                            inModList = true;
                            break;
                        }
                    }
                }

                // inclusionAngle is private, and it is precisely what tells us whether OnSetup ran.
                FieldInfo angleField = typeof(PQSMod_MapDecalVertexRemoveScatter)
                    .GetField("inclusionAngle", BindingFlags.Instance | BindingFlags.NonPublic);
                double inclusionAngle = angleField != null ? (double)angleField.GetValue(decal) : -1.0;

                LOGGER.LogInfo($"  decal: enabled={decal.modEnabled} inModList={YesNo(inModList)} " +
                               $"order={decal.order} radius={decal.radius:F0} " +
                               $"inclusionAngle={inclusionAngle:E3} (={inclusionAngle * decal.sphere.radius:F0} m) " +
                               $"normPos={decal.normalisedPosition} mask={(decal.colorMap == null ? "NULL" : decal.colorMap.width + "px")} " +
                               $"blocked={decal.blockedScatters?.Count}");
            }
            LOGGER.LogInfo($"  our decals on this body: {found}");
        }

        private static string YesNo(bool value)
        {
            return value ? "YES" : "NO";
        }

        // What Parallax made of it: the per-quad registry, and the quad the vessel actually sits on.
        private void DumpParallaxState(CelestialBody body, Vessel vessel)
        {
            LOGGER.LogInfo($"  maskedScattersPerQuad={PQSMod_MapDecalVertexRemoveScatter.maskedScattersPerQuad.Count} " +
                           $"scatterQuadData={ScatterComponent.scatterQuadData.Count}");

            // The quad under the vessel, found in PLANET frame — the same space the decal compares against
            // (quad.positionPlanetRelative). Comparing Unity transforms instead would mix reference frames.
            Vector3d vesselDir = body.GetRelSurfaceNVector(vessel.latitude, vessel.longitude);
            PQ closest = null;
            ScatterSystemQuadData closestData = null;
            double closestAngle = double.MaxValue;
            foreach (var entry in ScatterComponent.scatterQuadData)
            {
                PQ quad = entry.Key;
                if (quad == null || quad.gameObject == null)
                {
                    continue;
                }
                double angle = System.Math.Acos(Vector3d.Dot(quad.positionPlanetRelative, vesselDir));
                if (angle < closestAngle)
                {
                    closestAngle = angle;
                    closest = quad;
                    closestData = entry.Value;
                }
            }
            double closestDist = closestAngle * body.Radius;

            if (closest == null)
            {
                LOGGER.LogWarning("  no Parallax quad data at all — scatters are not managed here?");
                return;
            }

            bool registered = PQSMod_MapDecalVertexRemoveScatter.maskedScattersPerQuad.ContainsKey(closest);
            LOGGER.LogInfo($"  nearest scatter quad: sub={closest.subdivision} dist={closestDist:F0} m " +
                           $"inRegistry={YesNo(registered)} blockMapPQSMod={(closestData.blockMapPQSMod == null ? "NULL" : "set")} " +
                           $"blockMapData={(closestData.blockMapData == null ? "NULL" : "set")}");

            int reading = 0;
            foreach (ScatterData data in closestData.quadScatters)
            {
                if (data.readBlockMap)
                {
                    reading++;
                }
            }
            LOGGER.LogInfo($"  scatters on that quad: {closestData.quadScatters.Count}, reading our block map: {reading}");
            foreach (ScatterData data in closestData.quadScatters)
            {
                LOGGER.LogInfo($"      {data.scatter.scatterName}: readBlockMap={data.readBlockMap}");
            }

            DumpNearestQuads(body, vesselDir);

            DumpRegistry(body, vesselDir);
            DumpAngleNow(body, closest);
        }

        // The quads Parallax tracks nearest to the vessel. If none is closer than the quad half-diagonal,
        // the quad actually carrying the vessel is missing from the scatter system entirely.
        private void DumpNearestQuads(CelestialBody body, Vector3d vesselDir)
        {
            // Snapshot into a local list: Parallax's dictionary is its live state and must not be touched.
            List<KeyValuePair<PQ, double>> ranked = new List<KeyValuePair<PQ, double>>();
            foreach (var entry in ScatterComponent.scatterQuadData)
            {
                PQ quad = entry.Key;
                if (quad == null || quad.gameObject == null)
                {
                    continue;
                }
                double angle = System.Math.Acos(System.Math.Min(1.0,
                    Vector3d.Dot(quad.positionPlanetRelative, vesselDir)));
                ranked.Add(new KeyValuePair<PQ, double>(quad, angle));
            }
            ranked.Sort((a, b) => a.Value.CompareTo(b.Value));

            // MEASURED on the nearest quad, never computed. The (pi/2 * R) / 2^maxLevel formula
            // UNDER-estimates a quad by up to 27% (a PQS is a projected cube, so quads are even on the face
            // plane, not in angle) — printing it as if it were the quad's size is how a log reader ends up
            // hunting the wrong distance. Measured on the Mun at maxLevel 9: formula 614 m, real 728 x 754 m
            // here, and 313 x 478 m near a cube corner. It stays only as a labelled reference point.
            QuadMetrics.QuadShape shape;
            if (ranked.Count > 0 && QuadMetrics.TryMeasureShape(body, ranked[0].Key, out shape))
            {
                LOGGER.LogInfo($"  nearest quad measured at maxLevel={body.pqsController.maxLevel}: " +
                               $"{shape.sideA:F0} x {shape.sideB:F0} m, half-diagonal " +
                               $"{shape.halfDiagonalMin:F0}..{shape.halfDiagonalMax:F0} m " +
                               $"(the {QuadMetrics.NominalQuadEdge(body.pqsController):F0} m formula " +
                               $"under-estimates — never size anything with it)");
            }
            else
            {
                LOGGER.LogInfo($"  quad shape not measurable at maxLevel={body.pqsController.maxLevel}");
            }

            int count = System.Math.Min(5, ranked.Count);
            for (int i = 0; i < count; i++)
            {
                PQ quad = ranked[i].Key;
                bool reg = PQSMod_MapDecalVertexRemoveScatter.maskedScattersPerQuad.ContainsKey(quad);
                LOGGER.LogInfo($"    near quad #{i}: sub={quad.subdivision} " +
                               $"dist={ranked[i].Value * body.Radius:F0} m inRegistry={YesNo(reg)} " +
                               $"visible={quad.isVisible} active={quad.isActive}");
            }
        }

        // Which quads DID register: their subdivision tells us whether attachment only works for the quads
        // built by the initial pass, or also for the ones created later by subdivision.
        private void DumpRegistry(CelestialBody body, Vector3d vesselDir)
        {
            foreach (var entry in PQSMod_MapDecalVertexRemoveScatter.maskedScattersPerQuad)
            {
                PQ quad = entry.Key;
                if (quad == null || quad.gameObject == null)
                {
                    LOGGER.LogInfo("    registered quad: <destroyed>");
                    continue;
                }
                double dist = System.Math.Acos(Vector3d.Dot(quad.positionPlanetRelative, vesselDir)) * body.Radius;
                LOGGER.LogInfo($"    registered quad: sub={quad.subdivision} dist={dist:F0} m " +
                               $"built={quad.isBuilt} subdivided={quad.isSubdivided}");
            }
        }

        // Would the quad under the vessel pass the inclusion test RIGHT NOW? If yes, the geometry is fine and
        // the miss happened at OnQuadPreBuild time (position not yet set), not because the decal is too small.
        private void DumpAngleNow(CelestialBody body, PQ quad)
        {
            foreach (Transform child in body.pqsController.transform)
            {
                if (child.name != DecalGoName)
                {
                    continue;
                }
                PQSMod_MapDecalVertexRemoveScatter decal = child.GetComponent<PQSMod_MapDecalVertexRemoveScatter>();
                if (decal == null)
                {
                    continue;
                }
                FieldInfo angleField = typeof(PQSMod_MapDecalVertexRemoveScatter)
                    .GetField("inclusionAngle", BindingFlags.Instance | BindingFlags.NonPublic);
                double inclusionAngle = angleField != null ? (double)angleField.GetValue(decal) : -1.0;
                double angle = System.Math.Acos(Vector3d.Dot(quad.positionPlanetRelative, decal.normalisedPosition));
                LOGGER.LogInfo($"  angle now: quad<->decal={angle:E3} ({angle * body.Radius:F0} m) vs " +
                               $"inclusion={inclusionAngle:E3} ({inclusionAngle * body.Radius:F0} m) => " +
                               $"wouldRegister={YesNo(angle < inclusionAngle)}; " +
                               $"quad.positionPlanetRelative={quad.positionPlanetRelative}");
            }
        }
    }
}
