using System.Collections.Generic;
using UnityEngine;
using com.github.lhervier.ksp.bulldozermod.terrain;

namespace com.github.lhervier.ksp.bulldozermod.persistence
{
    /// <summary>
    /// One blade pass, stored per-save (event-sourcing) and replayed at load: the strip to clear, plus the
    /// leaf quads it covers.
    ///
    /// The quads are captured at click time, in flight, because Parallax's scatter registry — which alone
    /// says which quads can carry scatters — only exists once the PQS is built. The geometry itself would be
    /// recomputable. They stay valid across loads as long as the terrain detail preset does not change,
    /// which is why <see cref="maxLevel"/> travels with them.
    ///
    /// The blade width travels with the pass: it is a player setting, so replaying an old action must use
    /// the width it was ordered with, not whatever the slider says now.
    /// </summary>
    public class BulldozerAction
    {
        public string bodyName;

        /// <summary>Where the strip starts: the vessel, when the pass was ordered.</summary>
        public double lat;
        public double lon;

        /// <summary>
        /// Direction the strip runs towards, in the frame of CelestialBody.GetRelSurfaceNVector — the same
        /// frame as the quad centers below. Deliberately not a heading in degrees: reconstructing one needs a
        /// local north, hence one more convention to keep in step with the origin.
        /// </summary>
        public Vector3d forward;

        /// <summary>Length (m) of the strip, from the vessel forwards.</summary>
        public double length;

        /// <summary>Width (m) of the strip, as chosen when the pass was ordered.</summary>
        public double width;

        /// <summary>PQS maxLevel when the quads below were captured. A change invalidates them.</summary>
        public int maxLevel;

        /// <summary>One impacted leaf quad: where it is, and how big it is.</summary>
        public struct ImpactedQuad
        {
            /// <summary>
            /// Center direction in body frame, straight from PQ.positionPlanetRelative. Deliberately not
            /// lat/lon: see QuadRef for why that round-trip is unsafe.
            /// </summary>
            public Vector3d direction;

            /// <summary>
            /// Half-diagonal (m) of THIS quad, measured in flight. Carried here because there are no quads
            /// to measure at load time, and it cannot be derived from maxLevel either — quads are neither
            /// square nor uniform across a cube face. See QuadLocator.HalfDiagonal. Without it the quad
            /// cannot be replayed: there is no safe formula to fall back on.
            /// </summary>
            public double halfDiagonal;
        }

        /// <summary>The leaf quads the strip covers.</summary>
        public readonly List<ImpactedQuad> quads = new List<ImpactedQuad>();

        /// <summary>
        /// Rebuilds the strip this action describes, on the given body. Returns false when the stored
        /// geometry is unusable, which no action written by the mod ever is.
        /// </summary>
        public bool TryGetBand(CelestialBody body, out SurfaceBand band)
        {
            // The origin goes back through lat/lon and the direction does not, yet both land in the same
            // frame: GetRelSurfaceNVector produces exactly the frame `forward` was captured in.
            Vector3d origin = body.GetRelSurfaceNVector(lat, lon);
            return SurfaceBand.TryCreate(body.Radius, origin, forward, length, width, out band);
        }

        public void Save(ConfigNode node)
        {
            node.AddValue("body", bodyName);
            node.AddValue("lat", lat);
            node.AddValue("lon", lon);
            node.AddValue("forward", ConfigNode.WriteVector(forward));
            node.AddValue("length", length);
            node.AddValue("width", width);
            node.AddValue("maxLevel", maxLevel);
            foreach (ImpactedQuad quad in quads)
            {
                ConfigNode quadNode = node.AddNode("QUAD");
                quadNode.AddValue("dir", ConfigNode.WriteVector(quad.direction));
                quadNode.AddValue("halfDiag", quad.halfDiagonal);
            }
        }

        /// <summary>Parses an ACTION node. Returns null if it has no body name or no direction.</summary>
        public static BulldozerAction Load(ConfigNode node)
        {
            BulldozerAction a = new BulldozerAction();
            a.bodyName = node.GetValue("body");
            node.TryGetValue("lat", ref a.lat);
            node.TryGetValue("lon", ref a.lon);
            node.TryGetValue("length", ref a.length);
            node.TryGetValue("width", ref a.width);
            node.TryGetValue("maxLevel", ref a.maxLevel);

            string forward = node.GetValue("forward");
            if (string.IsNullOrEmpty(a.bodyName) || string.IsNullOrEmpty(forward))
            {
                return null;
            }
            a.forward = ConfigNode.ParseVector3D(forward);

            foreach (ConfigNode quadNode in node.GetNodes("QUAD"))
            {
                string dir = quadNode.GetValue("dir");
                if (string.IsNullOrEmpty(dir))
                {
                    continue;
                }
                double halfDiagonal = 0.0;
                quadNode.TryGetValue("halfDiag", ref halfDiagonal);
                a.quads.Add(new ImpactedQuad
                {
                    direction = ConfigNode.ParseVector3D(dir),
                    halfDiagonal = halfDiagonal
                });
            }
            return a;
        }
    }
}
