namespace com.github.lhervier.ksp.bulldozermod.persistence
{
    /// <summary>
    /// One cleaning action, stored per-save (event-sourcing) and replayed at load. Holds only what the
    /// replay needs — where, on which body, and how wide — so it never depends on the live scene.
    /// </summary>
    public class BulldozerAction
    {
        public string bodyName;
        public double lat;
        public double lon;

        /// <summary>Radius (m) of the area actually cleared of scatters.</summary>
        public double radius;

        public void Save(ConfigNode node)
        {
            node.AddValue("body", bodyName);
            node.AddValue("lat", lat);
            node.AddValue("lon", lon);
            node.AddValue("radius", radius);
        }

        /// <summary>Parses an ACTION node. Returns null if it has no body name.</summary>
        public static BulldozerAction Load(ConfigNode node)
        {
            BulldozerAction a = new BulldozerAction();
            a.bodyName = node.GetValue("body");
            node.TryGetValue("lat", ref a.lat);
            node.TryGetValue("lon", ref a.lon);
            node.TryGetValue("radius", ref a.radius);
            if (string.IsNullOrEmpty(a.bodyName))
            {
                return null;
            }
            return a;
        }
    }
}
