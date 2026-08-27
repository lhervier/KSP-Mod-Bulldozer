using UnityEngine;
using Parallax.PQS_Mods;

namespace com.github.lhervier.ksp.bulldozermod.terrain
{
    /// <summary>A leaf quad, identified by the direction of its center in BODY FRAME.</summary>
    public struct QuadRef
    {
        /// <summary>
        /// Unit direction of the quad center, in the same frame as PQ.positionPlanetRelative — which is
        /// also the frame CelestialBody.GetRelSurfaceNVector produces.
        ///
        /// Deliberately NOT stored as lat/lon: converting through GetLatitude/GetLongitude goes via the
        /// body's ROTATING frame, while GetRelSurfaceNVector rebuilds from the FIXED one. Round-tripping
        /// through them offsets the decal by the body's current rotation — measured at 52 km on the Mun.
        /// </summary>
        public Vector3d direction;

        /// <summary>
        /// Distance (m) from the center of THIS quad to its furthest corner. Measured when the quad is
        /// captured, because there is no quad left to measure at load time, and it is what sizes the decal
        /// covering it.
        /// </summary>
        public double halfDiagonal;

        /// <summary>The decal that already owns this quad, or null. Only meaningful in flight.</summary>
        public PQSMod_MapDecalVertexRemoveScatter owner;
    }
}
