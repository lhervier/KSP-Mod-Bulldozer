using System.Collections.Generic;
using UnityEngine;

namespace com.github.lhervier.ksp.bulldozermod.persistence
{
    /// <summary>
    /// One cleaning action, stored per-save (event-sourcing) and replayed at load: a disk to clear, plus the
    /// leaf quads it covers.
    ///
    /// The quads are captured at click time, in flight, because they cannot be recomputed at load time —
    /// the terrain is not built yet. They stay valid across loads as long as the terrain detail preset does
    /// not change, which is why <see cref="maxLevel"/> travels with them.
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

        /// <summary>
        /// Half-diagonal (m) of the impacted leaf quads, measured in flight. Carried here because it cannot
        /// be recomputed at load time (no quads yet) and cannot be derived from maxLevel either — quads are
        /// neither square nor uniform across a cube face. See QuadLocator.MeasureQuadEdges.
        /// </summary>
        public double quadHalfDiagonal;

        /// <summary>Center direction (body frame) of each impacted leaf quad.</summary>
        public readonly List<Vector3d> quads = new List<Vector3d>();

        public void Save(ConfigNode node)
        {
            node.AddValue("body", bodyName);
            node.AddValue("lat", lat);
            node.AddValue("lon", lon);
            node.AddValue("radius", radius);
            node.AddValue("maxLevel", maxLevel);
            node.AddValue("quadHalfDiag", quadHalfDiagonal);
            foreach (Vector3d quad in quads)
            {
                // The raw direction, not lat/lon: see QuadLocator.QuadRef for why the round-trip is unsafe.
                node.AddNode("QUAD").AddValue("dir", ConfigNode.WriteVector(quad));
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
            node.TryGetValue("quadHalfDiag", ref a.quadHalfDiagonal);
            if (string.IsNullOrEmpty(a.bodyName))
            {
                return null;
            }

            foreach (ConfigNode quadNode in node.GetNodes("QUAD"))
            {
                string dir = quadNode.GetValue("dir");
                if (!string.IsNullOrEmpty(dir))
                {
                    a.quads.Add(ConfigNode.ParseVector3D(dir));
                }
            }
            return a;
        }
    }
}
