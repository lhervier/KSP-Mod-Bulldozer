using System.Collections.Generic;
using UnityEngine;

namespace com.github.lhervier.ksp.bulldozermod.persistence
{
    /// <summary>
    /// One cleaning action, stored per-save (event-sourcing) and replayed at load: a disk to clear, plus the
    /// leaf quads it covers.
    ///
    /// The quads are captured at click time, in flight, because Parallax's scatter registry — which alone
    /// says which quads can carry scatters — only exists once the PQS is built. The geometry itself would be
    /// recomputable. They stay valid across loads as long as the terrain detail preset does not change,
    /// which is why <see cref="maxLevel"/> travels with them.
    /// </summary>
    public class BulldozerAction
    {
        public string bodyName;
        public double lat;
        public double lon;

        /// <summary>Radius (m) of the area actually cleared of scatters.</summary>
        public double radius;

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

        /// <summary>The leaf quads the disk covers.</summary>
        public readonly List<ImpactedQuad> quads = new List<ImpactedQuad>();

        public void Save(ConfigNode node)
        {
            node.AddValue("body", bodyName);
            node.AddValue("lat", lat);
            node.AddValue("lon", lon);
            node.AddValue("radius", radius);
            node.AddValue("maxLevel", maxLevel);
            foreach (ImpactedQuad quad in quads)
            {
                ConfigNode quadNode = node.AddNode("QUAD");
                quadNode.AddValue("dir", ConfigNode.WriteVector(quad.direction));
                quadNode.AddValue("halfDiag", quad.halfDiagonal);
            }
        }

        /// <summary>Parses an ACTION node. Returns null if it has no body name.</summary>
        public static BulldozerAction Load(ConfigNode node)
        {
            BulldozerAction a = new BulldozerAction();
            a.bodyName = node.GetValue("body");
            node.TryGetValue("lat", ref a.lat);
            node.TryGetValue("lon", ref a.lon);
            node.TryGetValue("radius", ref a.radius);
            node.TryGetValue("maxLevel", ref a.maxLevel);
            if (string.IsNullOrEmpty(a.bodyName))
            {
                return null;
            }

            // Saves written before the half-diagonal became per-quad carry a single action-wide maximum.
            // It is >= every member's own value, so it stays a valid (merely generous) fallback.
            double legacyHalfDiagonal = 0.0;
            node.TryGetValue("quadHalfDiag", ref legacyHalfDiagonal);

            foreach (ConfigNode quadNode in node.GetNodes("QUAD"))
            {
                string dir = quadNode.GetValue("dir");
                if (string.IsNullOrEmpty(dir))
                {
                    continue;
                }
                double halfDiagonal = legacyHalfDiagonal;
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
