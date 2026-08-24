using System;
using UnityEngine;
using Parallax.PQS_Mods;
using com.github.lhervier.ksp.shared;

namespace com.github.lhervier.ksp.bulldozermod.parallax
{
    /// <summary>
    /// Parallax's scatter-exclusion decal, with its quad inclusion test made numerically safe.
    ///
    /// Everything else (the mask, the blocked scatter list, how the scatter system reads them) is inherited
    /// untouched: Parallax stores decals in maskedScattersPerQuad typed as the base class, so a subclass is
    /// picked up exactly like the real thing.
    ///
    /// WHY: the base class decides whether a quad is in range with
    ///     Math.Acos(Vector3d.Dot(quad.positionPlanetRelative, normalisedPosition)) &lt; inclusionAngle
    /// but `position` is a Vector3 (float, ~1e-7 relative precision) while positionPlanetRelative is a
    /// Vector3d. When the quad sits right under the decal the true angle is tiny (8e-5 rad for 16 m on the
    /// Mun, i.e. a cosine of 1 - 3.2e-9, far below float noise), so the dot product comes out slightly ABOVE
    /// 1, Acos returns NaN, and `NaN &lt; inclusionAngle` is false in C#. The decal then attaches to every quad
    /// around it but NOT to the one at its center — measured in game: 4 quads registered at 712-754 m, the
    /// one 16 m away missed. Parallax never hits this because its own decals (the KSC) are placed at
    /// arbitrary spots, never centered on a quad.
    /// </summary>
    public class BulldozerScatterDecal : PQSMod_MapDecalVertexRemoveScatter
    {
        private static readonly ModLogger LOGGER = new ModLogger("Decal");

        /// <summary>
        /// Full-precision direction of the decal center, in body frame. Set before the sphere builds; it
        /// replaces the float-rounded value the base class derives from <c>position</c>.
        /// </summary>
        public Vector3d preciseDirection;

        // Same formula as the base class's private inclusionAngle, recomputed here because we cannot read it.
        private double inclusionAngle;

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

            inclusionAngle = Math.Atan(radius / sphere.radius) * 4.0;
            LOGGER.LogInfo($"Setup on {sphere.name}: radius={radius:F0} m, " +
                           $"inclusion={inclusionAngle * sphere.radius:F0} m, blocked={blockedScatters?.Count}");
        }

        public override void OnQuadPreBuild(PQ quad)
        {
            if (AngleTo(quad) >= inclusionAngle)
            {
                return;
            }
            PQSMod_MapDecalVertexRemoveScatter owner;
            if (maskedScattersPerQuad.TryGetValue(quad, out owner))
            {
                // Parallax allows a single decal per quad, and the loser is simply skipped. Naming the owner
                // matters: near the KSC it is Parallax's own ParallaxMaskKSC (radius 1400 m, so a 5.6 km
                // reach), which explains an area that stubbornly refuses to be cleared.
                LOGGER.LogWarning($"Quad already owned by '{owner.gameObject.name}' " +
                                  $"(radius={owner.radius:F0} m) — our decal is ignored on it");
                return;
            }
            maskedScattersPerQuad.Add(quad, this);
        }

        public override void OnQuadDestroy(PQ quad)
        {
            // Unconditional: the angle test could reject a quad we did register (and the entry must go
            // whoever owns it, since the quad itself is gone).
            maskedScattersPerQuad.Remove(quad);
        }

        // Clamping before Acos is the whole point: without it a perfectly centered quad yields NaN.
        private double AngleTo(PQ quad)
        {
            double dot = Vector3d.Dot(quad.positionPlanetRelative, normalisedPosition);
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
