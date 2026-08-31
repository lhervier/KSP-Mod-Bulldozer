using UnityEngine;
using com.github.lhervier.ksp.shared;

namespace com.github.lhervier.ksp.bulldozermod.parallax
{
    /// <summary>
    /// Builds the materials the flight overlays draw with — the rocks a pass would remove, and the ground
    /// trace of the pass itself.
    ///
    /// It is a type of its own because WHICH shader is used is not a cosmetic detail here: one KSP shader
    /// turns an overlay into a screen-space artefact (see <see cref="ShaderCandidates"/>), and Shader.Find
    /// falls through silently. Both overlays therefore have to make the same choice, and to say out loud
    /// which one they landed on.
    /// </summary>
    public static class OverlayMaterial
    {
        private static readonly ModLogger LOGGER = new ModLogger("Overlay");

        // Tried in order, first hit wins. Which shaders a KSP install actually keeps is not guaranteed, so
        // the resolved name is logged: it is the first thing to check if nothing shows up. On a stock 1.12
        // install "Particles/Alpha Blended" is NOT there and falls through to the legacy one.
        //
        // "KSP/Alpha/Unlit Transparent" is DELIBERATELY ABSENT and must not be put back: drawing an overlay
        // with it leaves black patches on the rocks that survive into the following frames as silhouettes
        // frozen in screen space. Every other candidate here was tried in flight at the same spot, none
        // shows it. See CLAUDE.md.
        //
        // Only the first two are translucent, which is what an overlay wants; the last three hide what is
        // under them behind a flat red and are a last resort - a coarse overlay still beats none. The one we
        // land on in practice is lit, so an overlay on the night side goes dark with the ground it sits on.
        // That is a known limitation, not an oversight: it is the only translucent shader of the five that
        // does not produce the artefact above.
        private static readonly string[] ShaderCandidates =
        {
            "Legacy Shaders/Transparent/Diffuse",
            "Particles/Alpha Blended",
            "KSP/Unlit",
            "Unlit/Color",
            "Diffuse"
        };

        /// <summary>
        /// A material tinted <paramref name="color"/>, or null when this install ships none of the shaders
        /// above — in which case the caller has nothing to draw with and should turn itself off.
        ///
        /// <paramref name="usage"/> only names the caller in the log line.
        ///
        /// The result is flagged HideAndDontSave so it survives a scene change: KSP runs
        /// Resources.UnloadUnusedAssets there, which would otherwise collect it. Callers are expected to
        /// hold it in a static field and never destroy it.
        /// </summary>
        public static Material Create(Color color, string usage)
        {
            for (int i = 0; i < ShaderCandidates.Length; i++)
            {
                Shader shader = Shader.Find(ShaderCandidates[i]);
                if (shader == null)
                {
                    continue;
                }

                Material material = new Material(shader);
                material.hideFlags = HideFlags.HideAndDontSave;

                // Which of these a shader exposes depends on which one we landed on, so set whatever is there
                // rather than assume. A textured shader with no texture would otherwise draw black.
                if (material.HasProperty("_Color"))
                {
                    material.SetColor("_Color", color);
                }
                if (material.HasProperty("_TintColor"))
                {
                    material.SetColor("_TintColor", color);
                }
                if (material.HasProperty("_MainTex"))
                {
                    material.SetTexture("_MainTex", Texture2D.whiteTexture);
                }
                material.renderQueue = 3100;

                // Logged because the choice MATTERS beyond looks: one KSP shader turns an overlay into a
                // screen-space artefact (see ShaderCandidates). Shader.Find returning null moves us down the
                // list silently, so this line is the only way to know what is actually drawing.
                LOGGER.LogInfo($"{usage} material using shader '{ShaderCandidates[i]}' "
                               + $"(shader queue {shader.renderQueue})");
                return material;
            }

            LOGGER.LogError($"No usable shader found for the {usage} overlay");
            return null;
        }
    }
}
