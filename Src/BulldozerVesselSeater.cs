using System;
using UnityEngine;
using com.github.lhervier.ksp.shared;
using com.github.lhervier.ksp.bulldozermod.persistence;

namespace com.github.lhervier.ksp.bulldozermod
{
    /// <summary>
    /// Protects the active vessel's seating when its body is being flattened.
    ///
    /// The flatten itself (purge + inject + ResetAndWait) is done in BulldozerScenario.OnLoad, cold, while the
    /// vessel is packed. But that rebuild is ASYNC: when the vessel goes off rails, KSP's CheckGroundCollision
    /// raycasts down to seat it, and the flat mesh/collider may not be rebuilt yet -> the raycast misses, the
    /// vessel is mis-seated and the freshly-built flat terrain then appears ABOVE it -> it falls through and
    /// explodes (observed: radarAltitude -57 m, no colliders).
    ///
    /// So at onVesselGoOffRails (which fires just BEFORE CheckGroundCollision inside GoOffRails) we, for the
    /// active vessel:
    ///   1. set skipGroundPositioning = true so KSP does NOT seat it on the not-yet-rebuilt mesh — it stays at
    ///      its saved pose, i.e. ~hgt above the flat plane (flattenTo is the terrain that was under it), so it
    ///      just settles gently once the flat ground finishes building;
    ///   2. level it (align up to the local radial = the new flat ground normal), keeping heading, so it does
    ///      not keep its old slope attitude and topple.
    ///
    /// This does NOT rebuild anything (that is OnLoad's job). It only guards the seating.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class BulldozerVesselSeater : MonoBehaviour
    {
        private static readonly ModLogger LOGGER = new ModLogger("Seater");

        private bool _done;

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
            if (_done || vessel == null || vessel != FlightGlobals.ActiveVessel || vessel.mainBody == null)
            {
                return;
            }
            if (!BodyIsFlattened(vessel.mainBody.name))
            {
                return; // no flatten on this body -> let KSP seat normally
            }
            _done = true;
            try
            {
                vessel.skipGroundPositioning = true;
                LevelVessel(vessel);
                LOGGER.LogInfo("Seating guarded: ground-positioning skipped + vessel levelled");
            }
            catch (Exception e)
            {
                LOGGER.LogError("Seating guard failed: " + e);
            }
        }

        private static bool BodyIsFlattened(string bodyName)
        {
            BulldozerScenario scenario = BulldozerScenario.Instance;
            if (scenario == null)
            {
                return false;
            }
            foreach (BulldozerAction a in scenario.Actions)
            {
                if (a.bodyName == bodyName)
                {
                    return true;
                }
            }
            return false;
        }

        // Aligns the vessel's up axis to the local radial (the now-horizontal ground normal), keeping heading.
        private static void LevelVessel(Vessel vessel)
        {
            CelestialBody body = vessel.mainBody;
            Vector3d radialUp = (vessel.GetWorldPos3D() - body.position).normalized;
            Vector3 up = vessel.vesselTransform.up;
            Quaternion levelled = Quaternion.FromToRotation(up, (Vector3)radialUp) * vessel.vesselTransform.rotation;
            vessel.SetRotation(levelled);
        }
    }
}
