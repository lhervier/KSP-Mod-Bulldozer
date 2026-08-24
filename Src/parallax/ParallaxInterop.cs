using System.Collections.Generic;
using UnityEngine;
using Parallax;
using Parallax.PQS_Mods;
using com.github.lhervier.ksp.shared;

namespace com.github.lhervier.ksp.bulldozermod.parallax
{
    /// <summary>
    /// Everything this mod does with ParallaxContinued, in one place.
    ///
    /// The mod is compiled against ParallaxContinued.dll, so a Parallax API change breaks the build rather
    /// than the flight. Parallax is therefore a hard requirement: without it there is nothing to clear.
    /// Only <see cref="Available"/> may be called when Parallax might be missing — it deliberately touches
    /// no Parallax type, so the CLR never has to load one.
    ///
    /// The type we drive is PQSMod_MapDecalVertexRemoveScatter: center + radius + a colorMap mask + a list of
    /// scatter names to block. It never touches terrain height (no OnVertexBuildHeight, and it declares only
    /// requirements = MeshColorChannel).
    /// </summary>
    public static class ParallaxInterop
    {
        private static readonly ModLogger LOGGER = new ModLogger("ParallaxInterop");

        // Parallax ships as ParallaxContinued.dll but declares [assembly: KSPAssembly("Parallax", 1, 0)],
        // and KSP OVERWRITES LoadedAssembly.name with that attribute value (AssemblyLoader.cs:874, after
        // initialising it to the file name at :604). So the name KSP knows it by is "Parallax", while the
        // file name stays "ParallaxContinued". Both are accepted here, since either one identifies it.
        private const string ParallaxKspName = "Parallax";
        private const string ParallaxDllName = "ParallaxContinued";

        /// <summary>
        /// True when ParallaxContinued is loaded. Checked through KSP's assembly list rather than by touching
        /// a Parallax type, so callers can test this safely even when the mod is absent.
        /// </summary>
        public static bool Available
        {
            get
            {
                foreach (AssemblyLoader.LoadedAssembly assembly in AssemblyLoader.loadedAssemblies)
                {
                    if (assembly.name == ParallaxKspName || assembly.dllName == ParallaxDllName)
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        /// <summary>
        /// Adds a configured exclusion decal to <paramref name="host"/>. The caller owns the GameObject and
        /// must parent it under the body's PQS BEFORE the sphere builds — the decal registers itself per quad
        /// in OnQuadPreBuild.
        /// </summary>
        /// <param name="position">Direction from the body center (body frame, not world).</param>
        /// <param name="radius">Footprint radius in meters — the decal's reach, NOT the cleared radius.</param>
        /// <param name="order">PQSMod order.</param>
        /// <param name="mask">Mask sampled in the compute shader: red &lt; 0.5 blocks the scatter.</param>
        /// <param name="blockedScatters">Scatter names to block, in Parallax's "Body-Name" form.</param>
        public static PQSMod_MapDecalVertexRemoveScatter AddDecal(GameObject host, Vector3d position, double radius,
                                                                  int order, Texture2D mask,
                                                                  List<string> blockedScatters)
        {
            // Our subclass, not Parallax's own type: its quad inclusion test returns NaN for the quad right
            // under the decal, which is precisely the one we need. See BulldozerScatterDecal.
            BulldozerScatterDecal decal = host.AddComponent<BulldozerScatterDecal>();
            decal.preciseDirection = position;

            decal.position = (Vector3)position;
            decal.radius = radius;
            decal.angle = 0f;
            decal.colorMap = mask;
            decal.blockedScatters = blockedScatters;
            decal.order = order;
            decal.modEnabled = true;

            return decal;
        }

        /// <summary>
        /// Returns every scatter name Parallax declares for the given body (already in "Body-Name" form), or
        /// an empty list when it knows nothing about that body.
        /// </summary>
        public static List<string> GetScatterNames(string bodyName)
        {
            List<string> names = new List<string>();

            ParallaxScatterBody body;
            if (!ConfigLoader.parallaxScatterBodies.TryGetValue(bodyName, out body) || body.fastScatters == null)
            {
                LOGGER.LogWarning($"Parallax knows no scatter body named '{bodyName}'");
                return names;
            }

            // fastScatters is the array DetermineScatters() itself iterates, so blocking all of it blocks
            // everything Parallax can spawn on the body (shared scatters are reached through their parent).
            foreach (Scatter scatter in body.fastScatters)
            {
                if (scatter != null && !string.IsNullOrEmpty(scatter.scatterName))
                {
                    names.Add(scatter.scatterName);
                }
            }
            return names;
        }

        /// <summary>
        /// Clears Parallax's static quad -&gt; decal registry.
        ///
        /// The registry is static, so entries survive save changes and outlive the decals we destroy. A stale
        /// entry would make Parallax refuse a fresh decal on that quad ("Multiple ... on the same quad"), so
        /// this must run whenever we purge our decals.
        /// </summary>
        public static void ClearQuadRegistry()
        {
            PQSMod_MapDecalVertexRemoveScatter.maskedScattersPerQuad.Clear();
        }

        /// <summary>
        /// Destroys the mask texture held by a decal we created, if any. Textures are plain assets: destroying
        /// the decal's GameObject would leave the texture in memory (megabytes per reload).
        /// </summary>
        public static void DestroyMask(GameObject host)
        {
            PQSMod_MapDecalVertexRemoveScatter decal = host?.GetComponent<PQSMod_MapDecalVertexRemoveScatter>();
            if (decal?.colorMap != null)
            {
                Object.DestroyImmediate(decal.colorMap);
            }
        }
    }
}
