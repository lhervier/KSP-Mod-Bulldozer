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
            if (!TryGetFlattenTo(vessel.mainBody.name, out double flattenTo))
            {
                return; // no flatten on this body -> let KSP seat normally
            }
            _done = true;
            try
            {
                vessel.skipGroundPositioning = true;
                LevelVessel(vessel);
                RaiseAboveFlat(vessel, flattenTo);
                LOGGER.LogInfo("Seating guarded: skip + levelled + raised above flat plane");
            }
            catch (Exception e)
            {
                LOGGER.LogError("Seating guard failed: " + e);
            }
        }

        private static bool TryGetFlattenTo(string bodyName, out double flattenTo)
        {
            flattenTo = 0.0;
            BulldozerScenario scenario = BulldozerScenario.Instance;
            if (scenario == null)
            {
                return false;
            }
            foreach (BulldozerAction a in scenario.Actions)
            {
                if (a.bodyName == bodyName)
                {
                    flattenTo = a.flattenTo;
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

        // Clearance (m) we leave between the vessel's lowest point and the flat plane. The flat mesh/collider
        // is rebuilt asynchronously and may appear a frame or two after physics turns on; if the lowest point
        // is at/below the plane when that happens, Unity ejects the vessel violently. Sitting safely ABOVE it
        // means the collider appears below the vessel and it just drops onto the ready flat ground.
        private const double Clearance = 3.0;

        // Raises the vessel (only ever upward) along the radial so its lowest collider point sits Clearance
        // above flattenTo. Done after levelling (levelling changes which point is lowest).
        private static void RaiseAboveFlat(Vessel vessel, double flattenTo)
        {
            CelestialBody body = vessel.mainBody;
            Vector3d radialUp = (vessel.GetWorldPos3D() - body.position).normalized;
            double lowestAlt = LowestColliderAltitude(vessel, body);
            if (double.IsInfinity(lowestAlt))
            {
                return;
            }
            double targetLowest = flattenTo + Clearance;
            double raiseBy = targetLowest - lowestAlt;
            if (raiseBy <= 0.0)
            {
                return; // already clear
            }
            vessel.SetPosition(vessel.GetWorldPos3D() + radialUp * raiseBy);
        }

        // Lowest altitude reached by any (non-trigger) collider of the vessel, via its world-AABB corners.
        private static double LowestColliderAltitude(Vessel vessel, CelestialBody body)
        {
            double minAlt = double.PositiveInfinity;
            foreach (Part part in vessel.parts)
            {
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
                        }
                    }
                }
            }
            return minAlt;
        }
    }
}
