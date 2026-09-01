namespace com.github.lhervier.ksp.bulldozermod
{
    /// <summary>
    /// The values SEVERAL files have to agree on, and only those.
    ///
    /// Named with the mod prefix, like every other type here, because KSP-Shared already ships a
    /// <c>Constants</c> and its sources are compiled into this very DLL: a bare <c>Constants</c> under the
    /// mod's namespace would SHADOW the shared one for every file of the mod, and <c>Constants.ModName</c>
    /// would stop resolving.
    ///
    /// Deliberately not a dump of every constant in the mod: a number that one class alone decides belongs
    /// next to the code that reads it, with the paragraph explaining it — moving it here would separate the
    /// two and let the explanation rot. What lands here is a value that was literally written twice, where
    /// two copies drifting apart is a bug nobody would see: the overlay layer, the name our decal
    /// GameObjects are recognised by.
    /// </summary>
    public static class BulldozerConstants
    {
        /// <summary>
        /// Local Scenery: the layer the flight camera draws the terrain on, where KSP puts the terrain
        /// colliders it raycasts for Vessel.heightFromTerrain, and where Parallax puts its scatter
        /// colliders. Both overlays draw onto it, and every ground probe queries it.
        /// </summary>
        public const int TerrainLayer = 15;

        /// <summary>
        /// <see cref="TerrainLayer"/> as a Physics raycast mask. Parts live on other layers, so a probe
        /// cannot hit the vessel it starts from.
        /// </summary>
        public const int TerrainMask = 1 << TerrainLayer;

        /// <summary>
        /// Name of the GameObjects our decals live on, so a purge or a dump picks out OUR decals only —
        /// Parallax ships its own (the KSC mask) and those must survive untouched.
        /// </summary>
        public const string DecalGoName = "BulldozerScatterDecal";
    }
}
