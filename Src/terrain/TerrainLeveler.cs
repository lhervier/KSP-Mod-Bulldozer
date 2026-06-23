using System.Collections.Generic;
using UnityEngine;
using com.github.lhervier.ksp.shared;
using com.github.lhervier.ksp.bulldozermod.persistence;

namespace com.github.lhervier.ksp.bulldozermod.terrain
{
    /// <summary>
    /// Injects stock PQSMod_FlattenArea mods into the bodies' PQS so the terrain builds flat.
    ///
    /// CRITICAL: we do NOT rebuild the PQS ourselves. Rebuilding a LIVE sphere (RebuildSphere/ResetAndWait)
    /// while the active vessel sits on it is the documented dead-end — the vessel "holds" its tile so the quad
    /// under it is not rebuilt (a bump remains), neighbours rebuild partially (holes/cracks), and the result
    /// is incoherent. Instead we follow the stock / Kopernicus / Kerbal Konstructs pattern: have our mods
    /// present in the PQS mod list BEFORE the sphere builds. KSP itself rebuilds the active body's PQS at
    /// every flight load — FlightDriver -> PSystemSetup.SetPQSActive -> pqs.ForceStart() — while the vessel is
    /// still PACKED (cold, no tile held). ForceStart re-runs SetupMods, which collects + OnSetup's our child
    /// mods, so the quads build flat from the start. The vessel then seats on the already-flat terrain and
    /// CheckGroundCollision levels it on the flat (radial) normal by itself.
    ///
    /// => This must therefore run BEFORE SetPQSActive, i.e. from the scenario's OnLoad (during the loading
    /// screen), NOT from a flight addon that wakes up after the vessel is already seated.
    ///
    /// Because celestial bodies persist for the whole KSP session (DontDestroyOnLoad), our mods would leak
    /// from one save to another, so we PURGE all of our mods from every body first, then inject only the
    /// given actions. Call once per save load (incl. saves with 0 actions, to clear leaked deformations).
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
        /// Purges all of our flatten mods from every body (kills cross-save leaks), injects one
        /// PQSMod_FlattenArea per action, then ResetAndWait()s the touched bodies.
        ///
        /// The reset is required: KSP's ForceStart (StartSphere force:true) does NOT rebuild already-built
        /// quads, so quads built before our injection stay natural (the under-rover quad becomes a bump while
        /// freshly-built neighbours come out flat -> patchwork + cracks). ResetSphere destroys all quads so the
        /// following build (KSP's ForceStart, or our wait-coroutine) rebuilds them ALL fresh with our mod.
        ///
        /// We use ResetAndWait (not RebuildSphere): it resets, then WAITS until the sphere is alive before
        /// building, so it can't bail out early. Called from OnLoad (loading screen) the rebuild runs while the
        /// vessel is still PACKED, so the under-vessel quad is rebuilt cold (no "vessel holds its tile" bump).
        /// </summary>
        public static void InjectActions(IList<BulldozerAction> actions)
        {
            HashSet<CelestialBody> touched = new HashSet<CelestialBody>();

            // 1. Purge our mods everywhere. DestroyImmediate so they are gone before the next SetupMods collect.
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

            // 2. Inject the current save's actions as child mods (collected + OnSetup'd by the next build).
            int injected = 0;
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
                injected++;
            }

            // 3. Reset touched bodies so EVERY quad rebuilds fresh with the current mod set. Cold, because we
            //    run in OnLoad while the vessel is still packed.
            foreach (CelestialBody body in touched)
            {
                body.pqsController.ResetAndWait();
            }

            LOGGER.LogInfo($"Injected {injected} flatten mod(s); reset {touched.Count} body(ies) for cold rebuild");
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
