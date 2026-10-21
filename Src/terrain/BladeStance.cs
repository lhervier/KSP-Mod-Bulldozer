using UnityEngine;

namespace com.github.lhervier.ksp.mod.bulldozer.terrain
{
    /// <summary>
    /// How the blade sits over the ground right under it: how high its cutting edge rides, and how far it
    /// leans out of the ground plane.
    ///
    /// This is what turns the blade from a decoration into a tool the player has to place. A blade bolted
    /// anywhere on the rover, pointing anywhere, used to clear just as well as one lowered onto the ground;
    /// now it has to be brought down and levelled first, which is what the robotic hinges are for.
    ///
    /// Measured against the GROUND, not against the geoid: on a slope a properly mounted blade follows the
    /// slope, so a level-with-the-horizon test would refuse exactly the rover that is doing it right. That
    /// is a different question from the one <see cref="SurfaceBand"/> asks, which is whether the facing axis
    /// is a heading at all.
    /// </summary>
    public struct BladeStance
    {
        // How far above the edge the ray starts. A blade doing its job is dug INTO the ground, and a ray
        // started at the edge itself would begin inside the terrain collider and report no hit at all.
        private const float ProbeLift = 5f;

        // How far below that it keeps looking. Only the first half-metre decides anything, but the PAW
        // shows the value while the player lowers the blade, so it stays useful well before then.
        private const float ProbeReach = 100f;

        /// <summary>Metres from the cutting edge down to the ground. Negative when the edge is buried.</summary>
        public double clearance;

        /// <summary>Degrees between the blade's own up axis and the ground normal. Zero is flat on the
        /// ground, and it covers both pitch and roll: one axis against one normal says everything about how
        /// the blade lies.</summary>
        public double tilt;

        /// <summary>
        /// Measures the blade at <paramref name="edge"/>, whose own up axis is <paramref name="bladeUp"/>.
        /// Returns false when there is no ground under it — out of reach, or not built yet.
        /// </summary>
        public static bool TryMeasure(CelestialBody body, Vector3 edge, Vector3 bladeUp,
                                      out BladeStance stance)
        {
            stance = new BladeStance();
            if (body == null)
            {
                return false;
            }

            Vector3 up = (Vector3)FlightGlobals.getUpAxis(body, edge);
            RaycastHit hit;
            if (!Physics.Raycast(edge + up * ProbeLift, -up, out hit, ProbeLift + ProbeReach,
                                 BulldozerConstants.TerrainMask, QueryTriggerInteraction.Ignore))
            {
                return false;
            }

            stance.clearance = hit.distance - ProbeLift;
            stance.tilt = Vector3.Angle(bladeUp, hit.normal);
            return true;
        }
    }
}
