using System;
using System.Collections.Generic;
using UnityEngine;
using com.github.lhervier.ksp.shared;
using com.github.lhervier.ksp.bulldozermod.persistence;
using com.github.lhervier.ksp.bulldozermod.terrain;

namespace com.github.lhervier.ksp.bulldozermod
{
    /// <summary>
    /// Replays the current save's levelling actions when the active vessel is about to be seated, and re-seats
    /// that vessel flat on the new horizontal ground.
    ///
    /// WHY here and not in a "wait for FlightGlobals.ready" coroutine (the previous approach): that fired ~7 s
    /// AFTER KSP had already loaded, seated AND oriented the vessel on the ORIGINAL (sloped) terrain. The
    /// flatten then made the ground horizontal under a vessel still tilted to the old slope -> it dropped onto
    /// its downhill edge and toppled ("banane"). See CLAUDE.md.
    ///
    /// KSP seats a landed vessel in Vessel.GoOffRails() -> CheckGroundCollision() (a downward raycast against
    /// the quad MESH collider, then attitude-aligned to the hit normal). GameEvents.onVesselGoOffRails fires
    /// just BEFORE that raycast, still synchronously inside GoOffRails. We hook it and, for the active vessel:
    ///   1. purge + replay the flatten (always purge, even with 0 actions, to clear deformations that leak in
    ///      memory across saves via the PQS's DontDestroyOnLoad);
    ///   2. set skipGroundPositioning so KSP does NOT seat the vessel on the old mesh — crucial because
    ///      RebuildSphere is ASYNC (StartSphere -> UpdateSphere coroutine -> quads rebuilt over later frames),
    ///      so the flat collider is not ready for a same-frame raycast;
    ///   3. level the vessel ourselves (align its up to the local radial = the new flat ground normal),
    ///      keeping heading. The flat quad then rebuilds under the already-level vessel and it settles.
    ///
    /// NB: this re-seating logic needs in-game validation (async-rebuild timing). The diagnostic button dumps
    /// the resulting attitude/mesh so we can check it.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class BulldozerTerrainApplier : MonoBehaviour
    {
        private static readonly ModLogger LOGGER = new ModLogger("Applier");

        // Guard so we act only once (on the first off-rails of the active vessel after this scene loaded).
        private bool _applied;

        // Subscribe in Awake (earlier than Start) so we are listening before the active vessel unpacks.
        private void Awake()
        {
            GameEvents.onVesselGoOffRails.Add(OnVesselGoOffRails);
        }

        private void OnDestroy()
        {
            GameEvents.onVesselGoOffRails.Remove(OnVesselGoOffRails);
        }

        private void OnVesselGoOffRails(Vessel vessel)
        {
            if (_applied || vessel == null)
            {
                return;
            }
            // Only act for the vessel KSP is about to seat (the active one we flattened under).
            if (FlightGlobals.ActiveVessel == null || vessel != FlightGlobals.ActiveVessel)
            {
                return;
            }
            _applied = true;
            try
            {
                ApplyAndSeat(vessel);
            }
            catch (Exception e)
            {
                LOGGER.LogError("ApplyAndSeat failed: " + e);
            }
        }

        private void ApplyAndSeat(Vessel vessel)
        {
            BulldozerScenario scenario = BulldozerScenario.Instance;
            IList<BulldozerAction> actions = scenario != null
                ? scenario.Actions
                : new List<BulldozerAction>();

            // Always run: ApplyActions purges our flatten mods from every body first (kills cross-save leaks
            // that survive via the PQS DontDestroyOnLoad), then replays this save's actions.
            TerrainLeveler.ApplyActions(actions);

            // Is the active vessel's body actually being flattened? If not, let KSP seat it normally.
            bool flattenedHere = false;
            foreach (BulldozerAction a in actions)
            {
                if (a.bodyName == vessel.mainBody.name)
                {
                    flattenedHere = true;
                    break;
                }
            }
            if (!flattenedHere)
            {
                LOGGER.LogInfo("Purge only (no action on this body); KSP seats normally");
                return;
            }

            // The ground under the vessel is being flattened to a horizontal plane, but RebuildSphere is async
            // so KSP's same-frame raycast would still hit the OLD sloped mesh. Skip KSP's seating and level the
            // vessel ourselves so it ends up flat (not keeping its old slope attitude).
            vessel.skipGroundPositioning = true;
            LevelVessel(vessel);
            LOGGER.LogInfo("Flatten applied; vessel levelled and ground-positioning skipped");
        }

        // Rotates the vessel so its up axis matches the local radial (the normal of the now-horizontal ground),
        // preserving heading. This mirrors what Vessel.CheckGroundCollision does against the ground normal, but
        // against the flat normal instead of the old slope.
        private void LevelVessel(Vessel vessel)
        {
            CelestialBody body = vessel.mainBody;
            Vector3d radialUp = (vessel.GetWorldPos3D() - body.position).normalized;
            Vector3 up = vessel.vesselTransform.up;
            Quaternion levelled = Quaternion.FromToRotation(up, (Vector3)radialUp) * vessel.vesselTransform.rotation;
            vessel.SetRotation(levelled);
        }
    }
}
