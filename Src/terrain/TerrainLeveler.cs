using System.Collections.Generic;
using UnityEngine;
using com.github.lhervier.ksp.shared;
using com.github.lhervier.ksp.bulldozermod.persistence;

namespace com.github.lhervier.ksp.bulldozermod.terrain
{
    /// <summary>
    /// Applies levelling actions to the PQS terrain by injecting stock PQSMod_FlattenArea mods.
    ///
    /// Because celestial bodies persist for the whole KSP session (DontDestroyOnLoad), our mods would leak
    /// from one save to another. The contract is therefore: ApplyActions PURGES all of our mods from every
    /// body first, then replays only the given actions. Call it once per save load.
    /// </summary>
    public static class TerrainLeveler
    {
        private static readonly ModLogger LOGGER = new ModLogger("TerrainLeveler");

        // Name tag of the GameObjects we create, so we can find/remove only OUR flatten mods (stock bodies
        // already carry their own PQSMod_FlattenArea for the KSC etc.).
        private const string FlattenGoName = "BulldozerFlatten";

        // Width (m) of the smooth blend ring OUTSIDE the flat disk. Franc edges create a near-vertical mesh
        // face that, single-sided, is back-culled when viewed from below -> you see through it to the ocean.
        // A ring turns that into a connected slope (no see-through). In the ring, FlattenArea blends toward
        // the height accumulated so far, so it also smooths the boundary between two overlapping flattens.
        // Kept wide enough to span a vertex or two on coarse bodies (spacing up to ~190 m on Eve).
        private const double RingWidth = 300.0;

        /// <summary>
        /// Brings every body to the deformation state described by <paramref name="actions"/> : removes all
        /// of our flatten mods from every body (kills cross-save leaks), re-injects one per action, then
        /// rebuilds the touched bodies so both the mesh AND GetSurfaceHeight reflect the result.
        /// </summary>
        public static void ApplyActions(IList<BulldozerAction> actions)
        {
            HashSet<CelestialBody> touched = new HashSet<CelestialBody>();

            // 1. Purge our mods everywhere. DestroyImmediate so they are gone before the rebuild re-collects.
            foreach (CelestialBody body in FlightGlobals.Bodies)
            {
                if (body.pqsController == null)
                {
                    continue;
                }
                foreach (PQSMod_FlattenArea f in body.pqsController.GetComponentsInChildren<PQSMod_FlattenArea>(true))
                {
                    if (f.gameObject.name == FlattenGoName)
                    {
                        Object.DestroyImmediate(f.gameObject);
                        touched.Add(body);
                    }
                }
            }

            // 2. Replay the current save's actions.
            foreach (BulldozerAction a in actions)
            {
                CelestialBody body = FlightGlobals.GetBodyByName(a.bodyName);
                if (body == null || body.pqsController == null)
                {
                    LOGGER.LogWarning($"Skipping action on unknown/PQS-less body '{a.bodyName}'");
                    continue;
                }
                InjectFlattenArea(body, a.lat, a.lon, a.flattenTo, a.radius);
                touched.Add(body);
            }

            // 3. Rebuild every touched body. RebuildSphere re-runs SetupMods (refreshing the mods array, so
            //    no stale/destroyed references remain) and rebuilds the quads of the active body.
            foreach (CelestialBody body in touched)
            {
                body.pqsController.RebuildSphere();
            }

            LOGGER.LogInfo($"Applied {actions.Count} action(s); rebuilt {touched.Count} body(ies)");
        }

        // Builds a stock PQSMod_FlattenArea on a child GameObject of the body's PQS. It must be a child:
        // SetupMods()/GetChildMods() collect PQSMods from the PQS hierarchy's children at rebuild time.
        private static void InjectFlattenArea(CelestialBody body, double lat, double lon, double flattenTo, double radius)
        {
            GameObject go = new GameObject(FlattenGoName);
            go.transform.SetParent(body.pqsController.transform, false);

            PQSMod_FlattenArea flatten = go.AddComponent<PQSMod_FlattenArea>();
            flatten.sphere = body.pqsController;
            flatten.modEnabled = true;
            flatten.order = 1000;            // run after the height-generating mods so it overrides them

            // Centre on the vessel via lat/lon (OnSetup resolves the position vector; confirmed correctly
            // centred on Kerbin/Mun/Minmus/Eve).
            flatten.useLatLon = true;
            flatten.bodyName = body.name;
            flatten.body = body;
            flatten.lat = lat;
            flatten.lon = lon;
            flatten.alt = flattenTo;         // used only to place the centre point

            // Absolute target height : OnSetup turns it into flattenToRadius = sphere.radius + flattenTo.
            flatten.flattenTo = flattenTo;

            // Flat disk of `radius`, surrounded by a smooth blend ring (inner..outer) that ramps back to
            // the surrounding height instead of a vertical (see-through) cliff. smoothStart/End = 0 give
            // flat tangents at both ends -> a clean S-curve.
            flatten.innerRadius = radius;
            flatten.outerRadius = radius + RingWidth;
            flatten.smoothStart = 0.0;
            flatten.smoothEnd = 0.0;
        }
    }
}
