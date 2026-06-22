using System.Collections;
using UnityEngine;
using com.github.lhervier.ksp.shared;
using com.github.lhervier.ksp.bulldozermod.persistence;
using com.github.lhervier.ksp.bulldozermod.terrain;

namespace com.github.lhervier.ksp.bulldozermod
{
    /// <summary>
    /// On entering flight, replays the current save's levelling actions (purge + replay + rebuild).
    ///
    /// The flatten must run here (flight, PQS ready) and not in the scenario's OnLoad (too early — the PQS
    /// is not in a state where RebuildSphere takes effect). We do NOT reposition the vessel: every in-flight
    /// reposition attempt (CheckGroundCollision, GoOffRails) shattered it (you can't safely move a
    /// physics-active vessel). Instead the flatten targets the lowest ground under the vessel's footprint
    /// (see BulldozerPlugin.LowestGroundUnderVessel), so no wheel is ever buried and the vessel just settles
    /// gently onto the flat ground on its own. Perfect no-movement re-seating is deferred.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class BulldozerTerrainApplier : MonoBehaviour
    {
        private static readonly ModLogger LOGGER = new ModLogger("Applier");

        private IEnumerator Start()
        {
            while (!FlightGlobals.ready || FlightGlobals.ActiveVessel == null)
            {
                yield return null;
            }

            BulldozerScenario scenario = BulldozerScenario.Instance;
            if (scenario == null || scenario.Actions.Count == 0)
            {
                yield break;
            }

            TerrainLeveler.ApplyActions(scenario.Actions);
            LOGGER.LogInfo("Terrain applied");
        }
    }
}
