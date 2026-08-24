using System;
using System.Collections.Generic;
using UnityEngine;
using Parallax;
using Parallax.PQS_Mods;

namespace com.github.lhervier.ksp.bulldozermod.terrain
{
    /// <summary>
    /// Locates the leaf quads a cleaning disk covers, and describes them by position rather than by
    /// reference — PQ instances are destroyed and rebuilt constantly, their center is what persists.
    ///
    /// Only usable in flight, when quads exist. At load time the quads are not built yet and their geometry
    /// cannot be recomputed without reimplementing the PQS cube projection, which is why the impacted quads
    /// are captured at click time and stored in the action.
    /// </summary>
    public static class QuadLocator
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

            /// <summary>The decal that already owns this quad, or null. Only meaningful in flight.</summary>
            public PQSMod_MapDecalVertexRemoveScatter owner;
        }

        /// <summary>
        /// Measures a leaf quad's two edge lengths (m) along its own axes. Returns false when it has no
        /// usable neighbour (a cube face border).
        ///
        /// The two axes must be measured SEPARATELY: a quad is not square. On a projected cube the angular
        /// scale is 1/(1+t^2) radially but 1/sqrt(1+t^2) across, so at 45 deg (t=1) one side is 1.41x the
        /// other — measured on the Mun: neighbours at 313 m on one axis, 478 m on the other. Averaging them
        /// would test a square the quad does not have, and could miss a quad the disk really touches.
        ///
        /// Neither can they be computed: the obvious formula (pi/2 * R / 2^maxLevel) assumes quads are spread
        /// evenly in ANGLE, when they are even on the FACE PLANE. Measured on the Mun (maxLevel 9): 781 m at
        /// the pole (center of a face, the 4/pi maximum), 741 m near the equator, 472 m at 45 deg — against a
        /// formula that says 614 m everywhere.
        /// </summary>
        public static bool MeasureQuadEdges(CelestialBody body, PQ quad, out double alongEast, out double alongNorth)
        {
            alongEast = Distance(body, quad, quad.east, quad.west);
            alongNorth = Distance(body, quad, quad.north, quad.south);

            // A single axis is enough to stay honest: fall back to the other one rather than to an average.
            if (alongEast <= 0.0)
            {
                alongEast = alongNorth;
            }
            if (alongNorth <= 0.0)
            {
                alongNorth = alongEast;
            }
            return alongEast > 0.0 && alongNorth > 0.0;
        }

        // Distance to whichever of the two opposite neighbours is usable.
        private static double Distance(CelestialBody body, PQ quad, PQ first, PQ second)
        {
            PQ neighbour = Usable(quad, first) ?? Usable(quad, second);
            if (neighbour == null)
            {
                return 0.0;
            }
            return AngleBetween(quad.positionPlanetRelative, neighbour.positionPlanetRelative) * body.Radius;
        }

        private static PQ Usable(PQ quad, PQ neighbour)
        {
            return neighbour != null && neighbour.subdivision == quad.subdivision ? neighbour : null;
        }

        /// <summary>
        /// Rough edge length (m) of a leaf quad, from the sphere alone. Only for display or as a fallback:
        /// prefer <see cref="MeasureQuadEdge"/>, which is exact — see its remarks.
        /// </summary>
        public static double NominalQuadEdge(PQS sphere)
        {
            return (Math.PI * 0.5 * sphere.radius) / Math.Pow(2.0, sphere.maxLevel);
        }

        /// <summary>
        /// Returns the leaf quads a disk of <paramref name="radius"/> meters centered on
        /// <paramref name="lat"/>/<paramref name="lon"/> can touch, with their current owner when they
        /// already carry an exclusion decal.
        ///
        /// Quads come from Parallax's own registry: only quads it tracks can carry scatters, so any other
        /// quad is irrelevant to us.
        /// </summary>
        public static List<QuadRef> FindImpactedQuads(CelestialBody body, double lat, double lon, double radius,
                                                      out double halfDiagonal)
        {
            List<QuadRef> found = new List<QuadRef>();
            halfDiagonal = 0.0;

            PQS sphere = body.pqsController;
            if (sphere == null)
            {
                return found;
            }

            Vector3d center = body.GetRelSurfaceNVector(lat, lon);

            // Collect the leaf quads once, and find the one we stand on: the closest center.
            List<PQ> leaves = new List<PQ>();
            PQ host = null;
            double hostAngle = double.MaxValue;
            foreach (KeyValuePair<PQ, ScatterSystemQuadData> entry in ScatterComponent.scatterQuadData)
            {
                PQ quad = entry.Key;
                if (quad == null || quad.gameObject == null || quad.subdivision != sphere.maxLevel)
                {
                    continue;
                }
                leaves.Add(quad);

                double angle = AngleBetween(quad.positionPlanetRelative, center);
                if (angle < hostAngle)
                {
                    hostAngle = angle;
                    host = quad;
                }
            }
            if (host == null)
            {
                return found;
            }

            // Both edges are measured on the host quad: they differ, and they vary across a cube face.
            double alongEast, alongNorth;
            bool measured = MeasureQuadEdges(body, host, out alongEast, out alongNorth);
            if (!measured)
            {
                alongEast = alongNorth = NominalQuadEdge(sphere);
            }

            // Local tangent frame of the terrain, taken from the host's neighbours. Quads of a same face
            // share it closely enough over the ~1 km we look at.
            // Assigned unconditionally: the short-circuit below would otherwise leave them unset.
            Vector3d east = Vector3d.zero;
            Vector3d north = Vector3d.zero;
            bool exact = measured && BuildTangentFrame(host, out east, out north);

            double halfEast = alongEast * 0.5;
            double halfNorth = alongNorth * 0.5;
            halfDiagonal = Math.Sqrt(halfEast * halfEast + halfNorth * halfNorth);

            // Fallback when the frame could not be built (a cube face border): a conservative test, which may
            // select a quad the disk misses but never misses one it touches.
            double reach = halfDiagonal + radius;

            foreach (PQ quad in leaves)
            {
                bool touched;
                if (exact)
                {
                    // Point-to-square distance in the quad's own frame: the exact test, no margin needed.
                    Vector3d offset = center - quad.positionPlanetRelative;
                    double dx = Math.Abs(Vector3d.Dot(offset, east)) * body.Radius - halfEast;
                    double dy = Math.Abs(Vector3d.Dot(offset, north)) * body.Radius - halfNorth;
                    if (dx < 0.0)
                    {
                        dx = 0.0;
                    }
                    if (dy < 0.0)
                    {
                        dy = 0.0;
                    }
                    touched = dx * dx + dy * dy < radius * radius;
                }
                else
                {
                    touched = AngleBetween(quad.positionPlanetRelative, center) * body.Radius <= reach;
                }
                if (!touched)
                {
                    continue;
                }

                PQSMod_MapDecalVertexRemoveScatter owner;
                PQSMod_MapDecalVertexRemoveScatter.maskedScattersPerQuad.TryGetValue(quad, out owner);

                found.Add(new QuadRef
                {
                    direction = quad.positionPlanetRelative,
                    owner = owner
                });
            }
            return found;
        }

        /// <summary>
        /// Builds the terrain's local east/north axes at a quad, from one of its neighbours. Returns false
        /// when no neighbour of the same subdivision is available (a cube face border).
        /// </summary>
        private static bool BuildTangentFrame(PQ quad, out Vector3d east, out Vector3d north)
        {
            east = Vector3d.zero;
            north = Vector3d.zero;

            PQ reference = PickNeighbour(quad);
            if (reference == null)
            {
                return false;
            }

            Vector3d up = quad.positionPlanetRelative;
            Vector3d towards = reference.positionPlanetRelative - up;

            // Remove the radial part so the axis is truly tangent, then complete the frame with a cross
            // product — which makes the two axes orthogonal by construction.
            towards -= up * Vector3d.Dot(towards, up);
            if (towards.sqrMagnitude <= 0.0)
            {
                return false;
            }
            east = towards.normalized;
            north = Vector3d.Cross(up, east).normalized;
            return true;
        }

        private static PQ PickNeighbour(PQ quad)
        {
            PQ[] neighbours = { quad.east, quad.west, quad.north, quad.south };
            foreach (PQ neighbour in neighbours)
            {
                if (neighbour != null && neighbour.subdivision == quad.subdivision)
                {
                    return neighbour;
                }
            }
            return null;
        }

        /// <summary>Angle (rad) between two unit directions, safe against rounding past 1.</summary>
        public static double AngleBetween(Vector3d a, Vector3d b)
        {
            double dot = Vector3d.Dot(a, b);
            if (dot > 1.0)
            {
                dot = 1.0;
            }
            else if (dot < -1.0)
            {
                dot = -1.0;
            }
            return Math.Acos(dot);
        }

    }
}
