using UnityEngine;

namespace com.github.lhervier.ksp.bulldozermod.persistence
{
    /// <summary>
    /// One levelling action, stored per-save (event-sourcing). Replayed at load to rebuild the terrain
    /// deformation deterministically. Holds the RESOLVED target height (absolute, above the body datum)
    /// so the replay never needs the live scene.
    /// </summary>
    public class BulldozerAction
    {
        public string bodyName;
        public double lat;
        public double lon;
        public double radius;
        public double flattenTo;

        public void Save(ConfigNode node)
        {
            node.AddValue("body", bodyName);
            node.AddValue("lat", lat);
            node.AddValue("lon", lon);
            node.AddValue("radius", radius);
            node.AddValue("flattenTo", flattenTo);
        }

        /// <summary>Parses an ACTION node. Returns null if it has no body name.</summary>
        public static BulldozerAction Load(ConfigNode node)
        {
            BulldozerAction a = new BulldozerAction();
            a.bodyName = node.GetValue("body");
            node.TryGetValue("lat", ref a.lat);
            node.TryGetValue("lon", ref a.lon);
            node.TryGetValue("radius", ref a.radius);
            node.TryGetValue("flattenTo", ref a.flattenTo);
            if (string.IsNullOrEmpty(a.bodyName))
            {
                return null;
            }
            return a;
        }
    }
}
