using System.Collections.Generic;
using UnityEngine;
using com.github.lhervier.ksp.shared;
using com.github.lhervier.ksp.bulldozermod.parallax;
using com.github.lhervier.ksp.bulldozermod.persistence;

namespace com.github.lhervier.ksp.bulldozermod.terrain
{
    /// <summary>
    /// Applies the save's cleaning actions by injecting one Parallax exclusion decal per action into the
    /// bodies' PQS, then resetting the touched spheres so every quad rebuilds with the decals in place.
    ///
    /// Must run from the scenario's OnLoad, i.e. during the loading screen while the vessel is still PACKED:
    /// the decal registers itself per quad in OnQuadPreBuild, so it has to be in the PQS mod list BEFORE the
    /// sphere builds. KSP rebuilds the active body's PQS at every flight load (FlightDriver ->
    /// PSystemSetup.SetPQSActive -> pqs.ForceStart()), which re-runs SetupMods and collects our child mods.
    ///
    /// Bodies persist for the whole KSP session (PSystemManager is DontDestroyOnLoad), so our decals would
    /// leak from one save into the next. Every call therefore purges first, then injects only the actions
    /// given — including for saves with no action at all, which is what clears a leak.
    /// </summary>
    public static class ScatterCleaner
    {
        private static readonly ModLogger LOGGER = new ModLogger("ScatterCleaner");

        // Name of the GameObjects we create, so the purge removes OUR decals only — Parallax ships its own
        // (the KSC mask, for one) and those must survive untouched.
        private const string DecalGoName = "BulldozerScatterDecal";

        // Matches Parallax's own KSC decal. Anything high works: the exclusion is read by the scatter system
        // from a per-quad registry, not from the PQSMod ordering.
        private const int DecalOrder = 99999;

        /// <summary>
        /// Purges our decals from every body, injects one decal per action, and resets the touched bodies so
        /// their quads are rebuilt cold with the new mod set.
        /// </summary>
        public static void InjectActions(IList<BulldozerAction> actions)
        {
            if (!ParallaxInterop.Available)
            {
                LOGGER.LogWarning("ParallaxContinued is not installed — nothing to inject");
                return;
            }

            HashSet<CelestialBody> touched = new HashSet<CelestialBody>();

            Purge(touched);

            int injected = 0;
            foreach (BulldozerAction action in actions)
            {
                if (Inject(action))
                {
                    touched.Add(FlightGlobals.GetBodyByName(action.bodyName));
                    injected++;
                }
            }

            // Reset so EVERY quad is rebuilt and runs OnQuadPreBuild again with the current decals. Cold,
            // because we are still in the loading screen. ResetAndWait rather than RebuildSphere: it resets,
            // then waits for the sphere to be alive before building, so it cannot bail out early. In the
            // flight path its wait-coroutine is killed right after by ForceStart, which rebuilds synchronously.
            foreach (CelestialBody body in touched)
            {
                if (body?.pqsController != null)
                {
                    body.pqsController.ResetAndWait();
                }
            }

            LOGGER.LogInfo($"Injected {injected} decal(s); reset {touched.Count} body(ies)");
        }

        // DestroyImmediate, not Destroy: the objects must be gone before the next SetupMods() collects.
        private static void Purge(HashSet<CelestialBody> touched)
        {
            int purged = 0;
            List<GameObject> doomed = new List<GameObject>();
            foreach (CelestialBody body in FlightGlobals.Bodies)
            {
                if (body.pqsController == null)
                {
                    continue;
                }

                // Collect first: DestroyImmediate detaches the child right away, which would make an
                // in-flight Transform enumeration skip entries.
                doomed.Clear();
                foreach (Transform child in body.pqsController.transform)
                {
                    if (child.name == DecalGoName)
                    {
                        doomed.Add(child.gameObject);
                    }
                }

                foreach (GameObject go in doomed)
                {
                    ParallaxInterop.DestroyMask(go);
                    Object.DestroyImmediate(go);
                    touched.Add(body);
                    purged++;
                }
            }

            // Destroying a decal does not remove the quad entries it registered; a stale entry would make
            // Parallax reject the replacement decal on that quad.
            ParallaxInterop.ClearQuadRegistry();

            if (purged > 0)
            {
                LOGGER.LogInfo($"Purged {purged} decal(s) from {touched.Count} body(ies)");
            }
        }

        // Returns true when a decal was actually added.
        private static bool Inject(BulldozerAction action)
        {
            CelestialBody body = FlightGlobals.GetBodyByName(action.bodyName);
            if (body == null || body.pqsController == null)
            {
                LOGGER.LogWarning($"Skipping action on unknown/PQS-less body '{action.bodyName}'");
                return false;
            }

            List<string> blocked = ParallaxInterop.GetScatterNames(body.name);
            if (blocked.Count == 0)
            {
                LOGGER.LogWarning($"No Parallax scatter to block on '{body.name}' — skipping");
                return false;
            }

            PQS sphere = body.pqsController;
            double footprint = ScatterMask.FootprintRadius(sphere, action.radius);
            Texture2D mask = ScatterMask.CreateDiskMask(footprint, action.radius);

            // Child of the PQS: SetupMods()/GetChildMods() collect PQSMods from the sphere's children.
            GameObject go = new GameObject(DecalGoName);
            go.transform.SetParent(sphere.transform, false);

            // Body-frame direction, matching what the decal compares against (quad.positionPlanetRelative).
            // GetRelSurfaceNVector, not GetSurfaceNVector: the latter also applies the body's rotation.
            Vector3d position = body.GetRelSurfaceNVector(action.lat, action.lon);

            ParallaxInterop.AddDecal(go, position, footprint, DecalOrder, mask, blocked).sphere = sphere;

            LOGGER.LogInfo($"Decal on {body.name} lat={action.lat:F5} lon={action.lon:F5} " +
                           $"clear={action.radius:F0}m footprint={footprint:F0}m blocking {blocked.Count} scatter(s)");
            return true;
        }
    }
}
