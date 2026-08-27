using System;
using System.Collections.Generic;
using UnityEngine;
using Parallax;
using Parallax.PQS_Mods;
using com.github.lhervier.ksp.shared;
using com.github.lhervier.ksp.bulldozermod.utils;

namespace com.github.lhervier.ksp.bulldozermod.terrain
{
    /// <summary>
    /// Locates the leaf quads a cleaning disk covers, and describes them by position rather than by
    /// reference — PQ instances are destroyed and rebuilt constantly, their center is what persists.
    ///
    /// Only usable in flight, and the reason is Parallax, not geometry: the quads come from its own scatter
    /// registry, which exists only once the PQS is built. A quad's shape IS recomputable from the PQS cube
    /// projection (see <see cref="TryGetCorners"/>) — its Parallax registration is not. That is why the
    /// impacted quads are captured at click time and stored in the action.
    /// </summary>
    public static class QuadLocator
    {
        private static readonly ModLogger LOGGER = new ModLogger("QuadLocator");

        // The reference patch every quad is built from spans [-0.5, +0.5] on X and Z (PQS.CreateCache, with
        // cacheMeshSize = 1); quadMatrix maps it onto the quad's cell of its cube face.
        //
        // Consecutive entries MUST share an edge. Slipping a diagonal in here would build planes that slice
        // across the quad instead of bounding it — TryGetEdgePlanes refuses that case rather than trust it.
        private static readonly Vector3[] CornerLocal =
        {
            new Vector3(-0.5f, 0f, -0.5f),
            new Vector3( 0.5f, 0f, -0.5f),
            new Vector3( 0.5f, 0f,  0.5f),
            new Vector3(-0.5f, 0f,  0.5f)
        };

        /// <summary>
        /// Returns the leaf quads a disk of <paramref name="radius"/> meters centered on
        /// <paramref name="lat"/>/<paramref name="lon"/> touches — each with its own half-diagonal, which is
        /// what sizes the decal covering it at injection time — and with their current owner when they
        /// already carry an exclusion decal.
        ///
        /// Quads come from Parallax's own registry: only quads it tracks can carry scatters, so any other
        /// quad is irrelevant to us.
        /// </summary>
        public static List<QuadRef> FindImpactedQuads(CelestialBody body, double lat, double lon, double radius)
        {
            List<QuadRef> found = new List<QuadRef>();

            PQS sphere = body.pqsController;
            if (sphere == null)
            {
                return found;
            }

            Vector3d center = body.GetRelSurfaceNVector(lat, lon);

            // The disk becomes a spherical cap of angular radius radius/R. body.Radius rather than the local
            // terrain radius: it over-estimates the angle a little, which errs towards one quad too many.
            double sinAlpha = Math.Sin(radius / body.Radius);

            Vector3d[] corners = new Vector3d[4];
            Vector3d[] planes = new Vector3d[4];
            int skipped = 0;

            foreach (KeyValuePair<PQ, ScatterSystemQuadData> entry in ScatterComponent.scatterQuadData)
            {
                PQ quad = entry.Key;
                if (quad == null || quad.gameObject == null || quad.subdivision != sphere.maxLevel)
                {
                    continue;
                }

                Vector3d quadCenter = quad.positionPlanetRelative;
                if (!TryGetCorners(quad, corners) || !TryGetEdgePlanes(corners, quadCenter, planes))
                {
                    skipped++;
                    continue;
                }
                if (!Touches(planes, center, sinAlpha))
                {
                    continue;
                }

                PQSMod_MapDecalVertexRemoveScatter owner;
                PQSMod_MapDecalVertexRemoveScatter.maskedScattersPerQuad.TryGetValue(quad, out owner);

                found.Add(new QuadRef
                {
                    direction = quadCenter,
                    halfDiagonal = HalfDiagonal(body, corners, quadCenter),
                    owner = owner
                });
            }

            if (skipped > 0)
            {
                LOGGER.LogWarning($"{skipped} quad(s) ignored: unusable geometry (quadMatrix not set up?)");
            }
            return found;
        }

        /// <summary>
        /// Unit directions of a quad's four corners, in the frame of PQ.positionPlanetRelative. Returns
        /// false when the quad has no usable matrix yet.
        /// </summary>
        public static bool TryGetCorners(PQ quad, Vector3d[] corners)
        {
            if (quad == null || corners == null || corners.Length < 4)
            {
                return false;
            }

            for (int i = 0; i < 4; i++)
            {
                // quadMatrix already carries the face rotation, the cell offset and the subdivision scale,
                // so this lands straight on the cube face; normalising is the PQS's own projection.
                //
                // It cannot be stale on the quads we query: PQS.BuildQuad places a quad's 225 vertices with
                // this very matrix, and we only ever look at quads Parallax registered, which it does at
                // build time. A stale matrix would put a whole tile elsewhere on the planet.
                Vector3 onCube = quad.quadMatrix.MultiplyPoint3x4(CornerLocal[i]);
                Vector3d dir = new Vector3d(onCube.x, onCube.y, onCube.z);
                double len = dir.magnitude;
                if (len <= 0.0)
                {
                    return false;
                }
                corners[i] = dir / len;
            }
            return true;
        }

        /// <summary>
        /// Inward normals of the four great-circle planes that bound a quad.
        ///
        /// A straight edge on the cube face projects to an arc of a GREAT CIRCLE, because the directions
        /// through a straight segment span a plane containing the body center. So the projected quad is
        /// exactly the intersection of four half-spaces — no rectangle assumption, valid however skewed the
        /// quad is (the two grid axes are 90 deg apart only on a face's median lines, and 120 deg apart at a
        /// cube corner).
        ///
        /// Returns false when the corners are not in cycle order, which would produce meaningless planes.
        /// </summary>
        public static bool TryGetEdgePlanes(Vector3d[] corners, Vector3d quadCenter, Vector3d[] planes)
        {
            if (corners == null || planes == null || corners.Length < 4 || planes.Length < 4)
            {
                return false;
            }

            int inward = 0;
            for (int i = 0; i < 4; i++)
            {
                Vector3d n = Vector3d.Cross(corners[i], corners[(i + 1) & 3]);
                double len = n.magnitude;
                if (len <= 0.0)
                {
                    return false;
                }
                n = n / len;
                if (Vector3d.Dot(n, quadCenter) > 0.0)
                {
                    inward++;
                }
                planes[i] = n;
            }

            if (inward == 4)
            {
                return true;
            }

            // All four pointing outwards just means the corners cycle the other way round: flip them.
            if (inward == 0)
            {
                for (int i = 0; i < 4; i++)
                {
                    planes[i] = -planes[i];
                }
                return true;
            }

            // A MIX is the real error: two of the corners are diagonal neighbours, so at least one plane cuts
            // through the quad. Refuse rather than test against nonsense — this cannot happen with
            // CornerLocal as written, and exists to catch anyone reordering it.
            LOGGER.LogError($"Quad corners are not in cycle order ({inward}/4 planes inward) — quad ignored");
            return false;
        }

        /// <summary>
        /// Furthest a corner sits from the quad center, in meters. Exact, and needs no neighbour — unlike a
        /// measurement taken on PQ.north/east, it still works at the rim of the subdivided area.
        /// </summary>
        private static double HalfDiagonal(CelestialBody body, Vector3d[] corners, Vector3d quadCenter)
        {
            double max = 0.0;
            for (int i = 0; i < 4; i++)
            {
                double d = MathUtils.AngleBetween(quadCenter, corners[i]) * body.Radius;
                if (d > max)
                {
                    max = d;
                }
            }
            return max;
        }

        // A cap of angular radius asin(sinAlpha) is entirely outside a half-space when its center sits
        // further than 90 deg + that radius from the inward normal. Conservative at the corners: a cap
        // sitting diagonally past one may clear all four tests without actually touching the quad. That way
        // round is harmless (one useless decal); the other way would leave scatters behind.
        private static bool Touches(Vector3d[] planes, Vector3d center, double sinAlpha)
        {
            for (int i = 0; i < 4; i++)
            {
                if (Vector3d.Dot(center, planes[i]) < -sinAlpha)
                {
                    return false;
                }
            }
            return true;
        }
    }
}
