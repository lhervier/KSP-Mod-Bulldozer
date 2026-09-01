using System.Collections.Generic;
using UnityEngine;
using com.github.lhervier.ksp.shared;
using com.github.lhervier.ksp.bulldozermod.parallax;
using com.github.lhervier.ksp.bulldozermod.persistence;

namespace com.github.lhervier.ksp.bulldozermod.terrain
{
    /// <summary>
    /// Applies the save's blade passes: one exclusion decal per impacted leaf quad, each carrying a mask
    /// where every strip touching that quad has been drawn.
    ///
    /// Merging is a lookup, not an algorithm: two passes touching the same quad share its decal and simply
    /// draw into the same mask.
    ///
    /// Must run from the scenario's OnLoad, i.e. during the loading screen while the vessel is still PACKED:
    /// a decal registers itself in OnQuadPreBuild, so it has to be in the PQS mod list BEFORE the sphere
    /// builds. KSP rebuilds the active body's PQS at every flight load (FlightDriver ->
    /// PSystemSetup.SetPQSActive -> pqs.ForceStart()), which re-runs SetupMods and collects our child mods.
    ///
    /// Bodies persist for the whole KSP session (PSystemManager is DontDestroyOnLoad), so our decals would
    /// leak from one save into the next. Every call therefore purges first, then injects only the actions
    /// given — including for saves with no action at all, which is what clears a leak.
    /// </summary>
    public static class ScatterCleaner
    {
        private static readonly ModLogger LOGGER = new ModLogger("ScatterCleaner");

        // Matches Parallax's own KSC decal. Anything high works: the exclusion is read from a per-quad
        // registry, not from the PQSMod ordering.
        private const int DecalOrder = 99999;

        // The decal must cover its whole quad, whose furthest point is one half-diagonal away; the margin
        // keeps the quad inside the shader's inscribed disk rather than exactly on its edge.
        //
        // It does NOT try to also contain the strip: a pass is longer than a quad, so DrawBand clamps it.
        // That is correct — the clipped part lies outside the quad, and the neighbouring quad's own mask
        // carries it.
        public const double FootprintMargin = 1.1;

        /// <summary>
        /// The strip as it will land in the mask a given quad receives: same footprint, same cell size, same
        /// orientation as <see cref="InjectActions"/> builds. Anything that wants to predict a pass — the
        /// quad search, the highlight — has to go through here, or it predicts a grid that will not exist.
        /// </summary>
        public static BandRaster RasterFor(CelestialBody body, Vector3d quadCenter, double halfDiagonal,
                                           SurfaceBand band)
        {
            double footprint = halfDiagonal * FootprintMargin;
            return BandRaster.Project(
                BulldozerScatterDecal.RotationFor(quadCenter, ParallaxInterop.DecalAngle),
                body.Radius, footprint, ScatterMask.SizeFor(footprint), band);
        }

        /// <summary>
        /// Purges our decals from every body, rebuilds one decal per impacted quad, and resets the touched
        /// bodies so their quads are rebuilt cold with the new mod set.
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

            // One decal per (body, quad center). The key rounds the coordinates so the same quad, described
            // by two different actions, lands on the same decal — that is the whole merge.
            Dictionary<string, BulldozerScatterDecal> decals = new Dictionary<string, BulldozerScatterDecal>();

            int strokes = 0;
            foreach (BulldozerAction action in actions)
            {
                CelestialBody body = FlightGlobals.GetBodyByName(action.bodyName);
                if (body == null || body.pqsController == null)
                {
                    LOGGER.LogWarning($"Skipping action on unknown/PQS-less body '{action.bodyName}'");
                    continue;
                }
                if (action.maxLevel != 0 && action.maxLevel != body.pqsController.maxLevel)
                {
                    LOGGER.LogWarning($"Action on {body.name} was recorded at maxLevel={action.maxLevel} but " +
                                      $"the body is now at {body.pqsController.maxLevel} — the terrain detail " +
                                      "preset changed, its quads no longer match, action skipped");
                    continue;
                }

                SurfaceBand band;
                if (!action.TryGetBand(body, out band))
                {
                    LOGGER.LogWarning($"Action on {body.name} carries unusable geometry " +
                                      $"(length={action.length:F0} m, forward={action.forward}) — skipped");
                    continue;
                }

                foreach (BulldozerAction.ImpactedQuad quad in action.quads)
                {
                    BulldozerScatterDecal decal = GetOrCreateDecal(body, quad, decals);
                    if (decal == null)
                    {
                        continue;
                    }
                    decal.mask.DrawBand(decal.rot, body.Radius, band);
                    strokes++;
                }
                touched.Add(body);
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

            LOGGER.LogInfo($"Injected {decals.Count} decal(s) carrying {strokes} stroke(s); " +
                           $"reset {touched.Count} body(ies)");
        }

        // Returns the decal owning that quad, creating it (with an empty mask) on first use.
        //
        // The first action to reach a quad is the one that sizes its decal, and later ones take it as it is.
        // That is safe because the half-diagonal describes the QUAD, not the action: every action touching
        // this quad measured the same geometry, so they all ask for the same footprint.
        private static BulldozerScatterDecal GetOrCreateDecal(CelestialBody body,
                                                              BulldozerAction.ImpactedQuad quad,
                                                              Dictionary<string, BulldozerScatterDecal> decals)
        {
            Vector3d quadCenter = quad.direction;

            // Rounding the direction gives quad centers described by two different actions the same key —
            // that is the whole merge. 9 decimals on a unit vector is millimetric on any body.
            string key = $"{body.name}:{quadCenter.x:F9}:{quadCenter.y:F9}:{quadCenter.z:F9}";
            BulldozerScatterDecal decal;
            if (decals.TryGetValue(key, out decal))
            {
                return decal;
            }

            PQS sphere = body.pqsController;
            List<string> blocked = ParallaxInterop.GetScatterNames(body.name);
            if (blocked.Count == 0)
            {
                LOGGER.LogWarning($"No Parallax scatter to block on '{body.name}' — skipping");
                return null;
            }

            // The half-diagonal is measured in flight and travels with the quad. There is NO fallback on
            // purpose: the only formula available at load time under-estimates a quad by up to 27% (a PQS is
            // a projected cube), so it would build a mask too small for its own quad, silently.
            if (quad.halfDiagonal <= 0.0)
            {
                LOGGER.LogWarning($"Quad on '{body.name}' carries no half-diagonal — quad skipped");
                return null;
            }
            double footprint = quad.halfDiagonal * FootprintMargin;

            // Child of the PQS: SetupMods()/GetChildMods() collect PQSMods from the sphere's children.
            GameObject go = new GameObject(BulldozerConstants.DecalGoName);
            go.transform.SetParent(sphere.transform, false);

            // The quad center comes straight from PQ.positionPlanetRelative, in the very frame the decal
            // compares against — no lat/lon round-trip, which would offset it by the body's rotation.
            decal = ParallaxInterop.AddDecal(go, quadCenter, footprint, DecalOrder, blocked);
            decal.sphere = sphere;
            decal.mask = new ScatterMask(footprint);
            decal.colorMap = decal.mask.Texture;

            // The PQS runs OnSetup itself at build time, but rot and normalisedPosition are needed NOW to
            // project the disks into the mask.
            decal.OnSetup();

            decals[key] = decal;
            LOGGER.LogInfo($"Decal on {body.name} quad dir={quadCenter} " +
                           $"footprint={footprint:F0} m blocking {blocked.Count} scatter(s)");
            return decal;
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
                    if (child.name == BulldozerConstants.DecalGoName)
                    {
                        doomed.Add(child.gameObject);
                    }
                }

                foreach (GameObject go in doomed)
                {
                    BulldozerScatterDecal decal = go.GetComponent<BulldozerScatterDecal>();
                    if (decal != null && decal.mask != null)
                    {
                        decal.mask.Destroy();
                    }
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
    }
}
