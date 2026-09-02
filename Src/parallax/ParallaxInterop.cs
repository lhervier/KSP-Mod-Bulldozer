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

        // Answered once per KSP session, then cached. The answer cannot change while the game runs (KSP
        // builds its assembly list at startup and never adds to it), and the scan is not free: it walks
        // 100 to 400 entries, through IEnumerable<LoadedAssembly>, so each call boxed an enumerator. That
        // was the mod's only permanent per-frame garbage, since a blade tests this in every OnUpdate.
        private static bool? _available;

        /// <summary>
        /// True when ParallaxContinued is loaded. Checked through KSP's assembly list rather than by touching
        /// a Parallax type, so callers can test this safely even when the mod is absent.
        ///
        /// In practice always true: [KSPAssemblyDependency("Parallax")] means KSP discards this mod outright
        /// when Parallax is missing. The guard is kept as a belt-and-braces measure.
        /// </summary>
        public static bool Available
        {
            get
            {
                if (!_available.HasValue)
                {
                    _available = Scan();
                }
                return _available.Value;
            }
        }

        /// <summary>
        /// Looks ParallaxContinued up in KSP's assembly list.
        /// </summary>
        private static bool Scan()
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

        /// <summary>
        /// Extra rotation given to every decal we create. Zero: the mask is ours to draw, so there is nothing
        /// to align it to (Parallax sets 15 degrees on its KSC decal to match a hand-made texture).
        ///
        /// Public because predicting the mask grid means reproducing the decal's `rot`, which folds it in.
        /// </summary>
        public const float DecalAngle = 0f;

        /// <summary>
        /// Adds an exclusion decal to <paramref name="host"/>, centered on <paramref name="position"/>. The
        /// caller owns the GameObject, must parent it under the body's PQS BEFORE the sphere builds, and
        /// must set the mask (<c>colorMap</c>) before the terrain is built.
        /// </summary>
        /// <param name="position">Direction from the body center (body frame, not world).</param>
        /// <param name="radius">Footprint radius in meters — the decal's reach, NOT the cleared radius.</param>
        /// <param name="order">PQSMod order.</param>
        /// <param name="blockedScatters">Scatter names to block, in Parallax's "Body-Name" form.</param>
        public static BulldozerScatterDecal AddDecal(GameObject host, Vector3d position, double radius,
                                                     int order, List<string> blockedScatters)
        {
            // Our subclass, not Parallax's own type: it attaches to a single quad, and its quad inclusion
            // test survives a decal sitting exactly on a quad center. See BulldozerScatterDecal.
            BulldozerScatterDecal decal = host.AddComponent<BulldozerScatterDecal>();
            decal.preciseDirection = position;

            decal.position = (Vector3)position;
            decal.radius = radius;
            decal.angle = DecalAngle;
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
