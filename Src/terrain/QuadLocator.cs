using System;
using System.Collections.Generic;
using UnityEngine;
using Parallax;
using Parallax.PQS_Mods;
using com.github.lhervier.ksp.shared;
using com.github.lhervier.ksp.mod.bulldozer.utils;

namespace com.github.lhervier.ksp.mod.bulldozer.terrain
{
    /// <summary>
    /// Locates the leaf quads a blade pass covers, and describes them by position rather than by
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
        /// Returns the leaf quads <paramref name="band"/> actually clears cells on — each with its own
        /// half-diagonal, which is what sizes the decal covering it at injection time — and with their
        /// current owner when they already carry an exclusion decal.
        ///
        /// Quads come from Parallax's own registry: only quads it tracks can carry scatters, so any other
        /// quad is irrelevant to us.
        /// </summary>
        public static List<QuadRef> FindImpactedQuads(CelestialBody body, SurfaceBand band)
        {
            List<QuadRef> found = new List<QuadRef>();

            PQS sphere = body.pqsController;
            if (sphere == null)
            {
                return found;
            }

            // Shortlist with a cap that CONTAINS the whole strip, then let the exact per-cell test below
            // sort it out. Doing the strip-versus-spherical-quad intersection here would duplicate, in
            // another form, the only test that decides anything: whether a cell of that quad's mask gets
            // cleared. body.Radius rather than the local terrain radius: it over-estimates the angle a
            // little, which errs towards one quad too many.
            Vector3d center = band.Middle;
            double sinAlpha = Math.Sin(band.CoarseRadius / body.Radius);

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

                // The exact test, in the very mask the injection will build: a quad the strip merely passes
                // near carries no cleared cell, hence no decal. It also keeps the shortlist above free to be
                // as generous as it likes.
                double halfDiagonal = HalfDiagonal(body, corners, quadCenter);
                if (!ScatterCleaner.RasterFor(body, quadCenter, halfDiagonal, band).ClearsAnyCell())
                {
                    continue;
                }

                PQSMod_MapDecalVertexRemoveScatter owner;
                PQSMod_MapDecalVertexRemoveScatter.maskedScattersPerQuad.TryGetValue(quad, out owner);

                found.Add(new QuadRef
                {
                    direction = quadCenter,
                    halfDiagonal = halfDiagonal,
                    owner = owner,

                    // Copied out of the scratch buffer: the caller keeps the quad long after this loop has
                    // reused it. Only for the handful of quads a pass actually touches.
                    edgePlanes = new[] { planes[0], planes[1], planes[2], planes[3] }
                });
            }

            if (skipped > 0)
            {
                LOGGER.LogWarning($"{skipped} quad(s) ignored: unusable geometry (quadMatrix not set up?)");
            }
            return found;
        }

        /// <summary>
        /// The first of <paramref name="quads"/> another mod's decal already owns, or null when they are
        /// all clearable.
        ///
        /// One taken quad settles the whole pass, hence the first rather than a list: Parallax allows a
        /// single decal per quad, so a strip crossing even one of them cannot be cleared as asked.
        ///
        /// ⚠️ A pass spans SEVERAL quads — four is usual, more on a small body — and each carries its own
        /// owner, so several mods can be blocking at once. Only one is ever named, and WHICH one is not
        /// determined: the quads are enumerated from Parallax's registry, a dictionary they are added to
        /// and dropped from as the terrain streams in. So with two blockers the name the PAW shows can
        /// change from one refresh to the next. Accepted deliberately (Lionel, 2026-09-02) — the refusal
        /// is the same whoever is named, and no such second decal exists in stock + Parallax anyway, where
        /// ParallaxMaskKSC is the only one.
        /// </summary>
        public static PQSMod_MapDecalVertexRemoveScatter FindForeignOwner(IList<QuadRef> quads)
        {
            if (quads == null)
            {
                return null;
            }
            for (int i = 0; i < quads.Count; i++)
            {
                PQSMod_MapDecalVertexRemoveScatter foreign = quads[i].ForeignOwner;
                if (foreign != null)
                {
                    return foreign;
                }
            }
            return null;
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
        /// Whether a surface direction falls inside a quad, i.e. on the inward side of all four of the
        /// planes <see cref="TryGetEdgePlanes"/> produced for it.
        ///
        /// This is what tells which mask OWNS a patch of ground: a mask covers a footprint wider than its
        /// own quad, so the very same ground is drawn into two to four of them, and anything counting or
        /// drawing per cell has to attribute each cell to exactly one.
        ///
        /// A quad whose shape was never captured contains everything: callers are all better off
        /// over-including than dropping ground on the floor.
        /// </summary>
        public static bool Contains(Vector3d[] planes, Vector3d direction)
        {
            if (planes == null || planes.Length < 4)
            {
                return true;
            }
            for (int i = 0; i < 4; i++)
            {
                if (Vector3d.Dot(direction, planes[i]) < 0.0)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Half-diagonal (m) of a single quad — what sizes the mask covering it. Returns false when the quad
        /// has no usable matrix yet.
        ///
        /// <paramref name="corners"/> is a caller-owned scratch buffer of 4: this runs per quad per frame in
        /// the highlight, and allocating there is exactly what that loop avoids.
        /// </summary>
        public static bool TryGetHalfDiagonal(CelestialBody body, PQ quad, Vector3d[] corners,
                                              out double halfDiagonal)
        {
            halfDiagonal = 0.0;
            if (quad == null || !TryGetCorners(quad, corners))
            {
                return false;
            }
            halfDiagonal = HalfDiagonal(body, corners, quad.positionPlanetRelative);
            return true;
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
        // round is harmless (the per-cell test drops it); the other way would leave scatters behind.
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
