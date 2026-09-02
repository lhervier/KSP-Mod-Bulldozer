using UnityEngine;
using Parallax.PQS_Mods;
using com.github.lhervier.ksp.bulldozermod.parallax;

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

        /// <summary>
        /// The decal owning this quad when it belongs to ANOTHER mod, or null when the quad is free or
        /// already ours. Parallax allows one decal per quad and drops the loser silently, so a quad in
        /// this state can never take one of ours.
        ///
        /// Where that rule lives for everything working off a QuadRef: the PAW announces it, the click
        /// refuses on it, and the pass trace leaves the quad unpainted because of it. The highlighter asks
        /// Parallax's registry the same question directly, having no QuadRef to read it from.
        /// </summary>
        public PQSMod_MapDecalVertexRemoveScatter ForeignOwner
        {
            get { return owner is BulldozerScatterDecal ? null : owner; }
        }

        /// <summary>
        /// Inward normals of the four great-circle planes bounding this quad, as
        /// <see cref="QuadLocator.TryGetEdgePlanes"/> produces them: a direction is inside the quad when it
        /// sits on the positive side of all four.
        ///
        /// Carried per quad rather than recomputed, because it is only obtainable in flight (it comes from
        /// PQ.quadMatrix), and because it is what tells which mask OWNS a patch of ground — a mask covers a
        /// footprint larger than its own quad, so the same ground is drawn into several of them.
        /// </summary>
        public Vector3d[] edgePlanes;
    }
}
