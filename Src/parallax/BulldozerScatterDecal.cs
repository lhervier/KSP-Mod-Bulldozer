using System;
using UnityEngine;
using Parallax.PQS_Mods;
using com.github.lhervier.ksp.shared;
using com.github.lhervier.ksp.bulldozermod.terrain;

namespace com.github.lhervier.ksp.bulldozermod.parallax
{
    /// <summary>
    /// Parallax's scatter-exclusion decal, restricted to a SINGLE leaf quad — the one it is centered on.
    ///
    /// One decal per quad is what makes merging trivial: a quad either has our decal or it does not, so
    /// cleaning a new area is "complete this quad's mask, or create it". It also removes any chance of two
    /// of our decals fighting over a quad, since Parallax only accepts one per quad.
    ///
    /// Two things make this possible, and both are load-bearing:
    ///
    /// 1. Overriding OnQuadPreBuild. Left to the base class, a ~700 m decal would grab every quad within a
    ///    2.8 km cone (inclusionAngle is 4x the radius) and lock out its neighbours' decals.
    /// 2. Clamping the dot product before Acos. The decal sits EXACTLY on its quad's center, so the angle is
    ///    zero and `position` being a Vector3 (float) makes the dot come out just above 1 — Acos would
    ///    return NaN and the quad would never attach. See the README, "PIEGE 1".
    ///
    /// The quad is identified by the position of its center, never by reference: PQ instances are destroyed
    /// and recreated constantly, only their center persists.
    /// </summary>
    public class BulldozerScatterDecal : PQSMod_MapDecalVertexRemoveScatter
    {
        private static readonly ModLogger LOGGER = new ModLogger("Decal");

        // How close a quad center must be to ours to be "our" quad. Quad centers are hundreds of meters
        // apart, so anything small works; 5 m absorbs the float rounding of `position` without ambiguity.
        private const double CenterToleranceMeters = 5.0;

        /// <summary>
        /// Full-precision direction of the quad center this decal owns, in body frame. Set before the sphere
        /// builds; it replaces the float-rounded value the base class derives from <c>position</c>.
        /// </summary>
        public Vector3d preciseDirection;

        /// <summary>The mask this decal writes into. Owned by us, destroyed with the decal.</summary>
        public ScatterMask mask;

        private double _centerTolerance;

        public override void OnSetup()
        {
            base.OnSetup();

            if (preciseDirection.sqrMagnitude > 0.0)
            {
                normalisedPosition = preciseDirection.normalized;

                // rot is derived from normalisedPosition, so it has to follow (same expression as the base).
                rot = Quaternion.AngleAxis(angle, Vector3.up)
                      * Quaternion.FromToRotation((Vector3)normalisedPosition, Vector3.up);
            }

            _centerTolerance = CenterToleranceMeters / sphere.radius;
        }

        public override void OnQuadPreBuild(PQ quad)
        {
            if (quad.subdivision != sphere.maxLevel)
            {
                return;
            }
            if (QuadLocator.AngleBetween(quad.positionPlanetRelative, normalisedPosition) > _centerTolerance)
            {
                return;
            }

            PQSMod_MapDecalVertexRemoveScatter owner;
            if (maskedScattersPerQuad.TryGetValue(quad, out owner) && !ReferenceEquals(owner, this))
            {
                // Parallax allows a single decal per quad and the loser is skipped silently. Naming the owner
                // matters: near the KSC it is Parallax's own ParallaxMaskKSC, which explains an area that
                // stubbornly refuses to be cleared.
                LOGGER.LogWarning($"Quad already owned by '{owner.gameObject.name}' " +
                                  $"(radius={owner.radius:F0} m) — our decal is ignored on it");
                return;
            }
            maskedScattersPerQuad[quad] = this;
        }

        public override void OnQuadDestroy(PQ quad)
        {
            PQSMod_MapDecalVertexRemoveScatter owner;
            if (maskedScattersPerQuad.TryGetValue(quad, out owner) && ReferenceEquals(owner, this))
            {
                maskedScattersPerQuad.Remove(quad);
            }
        }
    }
}
