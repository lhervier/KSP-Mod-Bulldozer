using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Parallax;
using com.github.lhervier.ksp.shared;
using com.github.lhervier.ksp.bulldozermod.terrain;

namespace com.github.lhervier.ksp.bulldozermod.parallax
{
    /// <summary>
    /// Draws, every frame, an overlay over the collideable scatters that a clean would remove right now.
    /// Toggled from the PAW; nothing is drawn while it is off.
    ///
    /// Only collideable scatters can be shown, and that is the useful set anyway: they are the only ones
    /// Parallax describes instance by instance, and the only ones a rover can hit. The small families exist
    /// solely as GPU instances — a clean wipes them too, but there is nothing to point at.
    ///
    /// The unit shown is the scatter INSTANCE, which for the big families is a whole CLUSTER of boulders.
    /// That is deliberate: it is exactly the unit the block map removes or keeps. A cluster anchored outside
    /// the disk keeps every one of its boulders, including the ones reaching inside it — highlighting per
    /// instance turns that rule into something visible rather than into a surprise (see the cluster PIEGE in
    /// the README).
    ///
    /// Note the fully qualified <c>Parallax.CollisionManager</c> below: KSP ships a <c>CollisionManager</c>
    /// of its own in the global namespace, so the bare name silently resolves to the wrong type — and the
    /// compiler then reports the field as missing rather than the type as ambiguous.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class ScatterHighlighter : MonoBehaviour
    {
        private static readonly ModLogger LOGGER = new ModLogger("Highlight");

        /// <summary>
        /// Toggled by the PAW entry. Static because this addon is destroyed and rebuilt at every scene
        /// change, while the choice belongs to the player.
        /// </summary>
        public static bool Active;

        // Tried in order, first hit wins. Which shaders a KSP install actually keeps is not guaranteed, so
        // the resolved name is logged: it is the first thing to check if nothing shows up.
        private static readonly string[] ShaderCandidates =
        {
            "KSP/Alpha/Unlit Transparent",
            "KSP/Unlit",
            "Unlit/Color",
            "Particles/Alpha Blended",
            "Legacy Shaders/Transparent/Diffuse",
            "Diffuse"
        };

        // Local Scenery. The scatters carry no renderer of their own, so there is no layer to inherit; this
        // one is drawn by the flight camera and is where the terrain lives.
        private const int DrawLayer = 15;

        // Slightly inflated so the overlay sits just outside the rock rather than fighting it in the z-buffer.
        private const float Inflate = 1.03f;

        private static readonly Color HighlightColor = new Color(1f, 0.15f, 0.1f, 0.6f);

        // Static, and never destroyed: see HighlightMaterial().
        private static Material _material;

        // The activation summary is logged on the first frame that actually finds something: on the very
        // first frame Parallax has usually not filled its lists yet and everything reads zero.
        private bool _summaryLogged;
        private bool _warnedNoRegistry;
        private int _lastShown = -1;
        private int _lastTotal = -1;
        private int[] _loaded;
        private int[] _shown;

        private void Update()
        {
            if (!Active)
            {
                _summaryLogged = false;
                _warnedNoRegistry = false;
                _lastShown = -1;
                _lastTotal = -1;
                return;
            }

            // Draw ONLY on a settled flight scene. Active is static, so it survives a scene change and we
            // would otherwise keep drawing all through a reload, while quads and their GPU buffers are being
            // torn down and rebuilt — pointless at best.
            //
            // Was TRIED as a fix for the ghost-shadow bug (see the README) and did NOT fix it. Kept on its
            // own merits.
            if (!HighLogic.LoadedSceneIsFlight || !FlightGlobals.ready)
            {
                return;
            }

            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null || vessel.mainBody == null || vessel.packed)
            {
                return;
            }

            // Every collideable instance of every loaded quad — NOT CollisionManager.activeObjects, which
            // only holds the handful Parallax has actually built a collider for. Those appear only when the
            // vessel's bounding sphere is about to touch the rock's own (ProcessColliderJob, in essence
            // `|d|² - vesselBounds - sqrMeshSize - lookahead² < 0`), which reaches a few meters for a small
            // rock and a few tens for a big cluster — far too short, and far too uneven, to preview a zone.
            FastList<ScatterColliderData> collision = Parallax.CollisionManager.collisionData;
            if (collision == null)
            {
                if (!_warnedNoRegistry)
                {
                    LOGGER.LogWarning("Parallax exposes no collider data on this body — nothing to highlight");
                    _warnedNoRegistry = true;
                }
                return;
            }

            Material material = HighlightMaterial();
            if (material == null)
            {
                return;
            }

            // ONE camera, never null. `camera: null` submits the draw to EVERY camera in the game, and an
            // overlay only makes sense in the one being looked at; Parallax names Camera.main explicitly for
            // its own scatters.
            //
            // Was TRIED as a fix for the ghost-shadow bug (see the README) and did NOT fix it. Kept on its
            // own merits.
            Camera camera = Camera.main;
            if (camera == null)
            {
                return;
            }

            CelestialBody body = vessel.mainBody;

            // Both directions are taken in WORLD space at the same instant, which sidesteps the fixed vs
            // rotating frame trap entirely: no lat/lon round-trip, nothing to keep in sync. It describes the
            // same disk the click paints, since Vessel.latitude/longitude derive from this very position.
            Vector3d bodyCenter = body.position;
            Vector3d vesselWorld = vessel.GetWorldPos3D();
            Vector3d centerDir = (vesselWorld - bodyCenter).normalized;
            // Compared as a cosine rather than an angle: this runs on every collideable instance of every
            // loaded quad, every frame, and an Acos per instance is the one thing worth not paying for.
            double minCos = Math.Cos(BulldozerCleanModule.ClearRadius / body.Radius);

            EnsureCounters();

            int shown = 0;
            int total = 0;

            for (int c = 0; c < collision.Length; c++)
            {
                ScatterColliderData data = collision[c];
                if (data == null || data.disposed || !data.quadLocalData.IsCreated)
                {
                    continue;
                }

                ScatterSystemQuadData quadData = data.scatterSystemQuad;
                if (quadData == null || quadData.quad == null || quadData.quad.meshRenderer == null)
                {
                    continue;
                }

                // Reject whole quads before touching their instances. Without this the loop walks every
                // collideable instance Parallax has loaded — measured at 59 000 on the Mun for the 8 that
                // were actually in range. A quad matters only if it comes within the disk plus its own
                // width, which around the vessel leaves four quads at most.
                double quadReach = BulldozerCleanModule.ClearRadius + Math.Sqrt(quadData.sqrQuadWidth);
                if ((((Vector3d)quadData.quad.gameObject.transform.position) - vesselWorld).sqrMagnitude
                    > quadReach * quadReach)
                {
                    continue;
                }

                Scatter scatter = ScatterAt(data.collideableScattersIndex);

                // LOD0, not the collider mesh. The collider uses meshLOD1, which is a DIFFERENT model
                // (Mun-HugeRocks: "clusterhuge" rendered, "clusterhuge2" collided), so drawing the collider
                // mesh produces a shape that does not match the rock and leaves bits of it sticking out.
                // Within our radius the renderer is on LOD0 anyway — its ranges start at 150 m.
                Mesh mesh = scatter?.renderer?.meshLOD0;
                if (mesh == null)
                {
                    continue;
                }

                // Same transform chain CollisionManager.CreateGameObject applies, rebuilt here because we
                // draw instances it never instantiated.
                Matrix4x4 quadToWorld = quadData.quad.meshRenderer.localToWorldMatrix;
                Vector3 quadScale = quadData.quad.transform.lossyScale;
                bool alignToTerrain = scatter.distributionParams.alignToTerrainNormal == 1;
                Vector3 planetNormal = Vector3.Normalize(quadData.quad.gameObject.transform.position
                                                         - quadData.quad.sphereRoot.transform.position);

                NativeArray<PositionData> positions = data.quadLocalData;
                int count = Math.Min(data.dataCount, positions.Length);
                int slot = CounterSlot(data.collideableScattersIndex);

                for (int i = 0; i < count; i++)
                {
                    PositionData position = positions[i];

                    Vector3 world = quadToWorld.MultiplyPoint3x4(position.localPos);
                    total++;
                    _loaded[slot]++;

                    Vector3d rockDir = ((Vector3d)world - bodyCenter).normalized;
                    if (Vector3d.Dot(centerDir, rockDir) < minCos)
                    {
                        continue;
                    }

                    Vector3 normal = alignToTerrain ? quadData.GetTerrainNormal(position.index) : planetNormal;
                    Quaternion rotation = Quaternion.FromToRotation(Vector3.up, normal)
                                          * Quaternion.AngleAxis(position.rotation, Vector3.up);

                    // Shadows OFF, and this is not cosmetic: the short DrawMesh overload defaults to
                    // castShadows = true, so the overlay threw its own shadow onto the ground — thin black
                    // streaks next to each rock, sliding around in 3D as the sun and camera moved.
                    Graphics.DrawMesh(
                        mesh,
                        Matrix4x4.TRS(world, rotation, Vector3.Scale(quadScale, position.localScale) * Inflate),
                        material, DrawLayer, camera, 0, null, ShadowCastingMode.Off, false);

                    shown++;
                    _shown[slot]++;
                }
            }

            if (!_summaryLogged && total > 0)
            {
                LogSummary(body, shown, total);
                _summaryLogged = true;
            }
            else if (shown != _lastShown || total != _lastTotal)
            {
                // Only on change: silent while parked, one line per step while driving. This is what tells
                // "nothing is within the radius" (total > 0) apart from "Parallax has loaded nothing around
                // here at all" (total == 0) — the two look identical on screen.
                LOGGER.LogInfo($"Highlighting {shown} of {total} collideable instance(s)");
            }
            _lastShown = shown;
            _lastTotal = total;
        }

        // One breakdown per activation. It answers the two questions this overlay always raises: how much
        // Parallax has actually loaded around the vessel, and WHICH families it can never show — the
        // non-collideable ones are wiped by a clean just the same, they simply have no per-instance data.
        private void LogSummary(CelestialBody body, int shown, int total)
        {
            LOGGER.LogInfo($"Highlighting {shown} of {total} collideable instance(s) loaded around the " +
                           $"vessel, for a {BulldozerCleanModule.ClearRadius:F0} m clean");

            Scatter[] scatters = Parallax.CollisionManager.collideableScatters;
            for (int i = 0; i < _loaded.Length; i++)
            {
                if (_loaded[i] == 0)
                {
                    continue;
                }
                string name = scatters != null && i < scatters.Length && scatters[i] != null
                    ? scatters[i].scatterName
                    : "slot " + i;
                LOGGER.LogInfo($"    {name}: {_shown[i]} highlighted / {_loaded[i]} loaded");
            }

            // Everything a clean removes, minus what we can draw: the difference is what the player will see
            // vanish without ever having been highlighted.
            foreach (string name in ParallaxInterop.GetScatterNames(body.name))
            {
                bool collideable = false;
                if (scatters != null)
                {
                    for (int i = 0; i < scatters.Length && !collideable; i++)
                    {
                        collideable = scatters[i] != null && scatters[i].scatterName == name;
                    }
                }
                if (!collideable)
                {
                    LOGGER.LogInfo($"    {name}: not collideable — removed by a clean, never highlighted");
                }
            }
        }

        // Null rather than an exception if Parallax's arrays ever disagree in length.
        private static Scatter ScatterAt(int index)
        {
            Scatter[] scatters = Parallax.CollisionManager.collideableScatters;
            return scatters != null && index >= 0 && index < scatters.Length ? scatters[index] : null;
        }

        private int CounterSlot(int collideableIndex)
        {
            return collideableIndex >= 0 && collideableIndex < _loaded.Length ? collideableIndex : 0;
        }

        private void EnsureCounters()
        {
            Scatter[] scatters = Parallax.CollisionManager.collideableScatters;
            int slots = Math.Max(1, scatters == null ? 1 : scatters.Length);

            if (_loaded == null || _loaded.Length < slots)
            {
                _loaded = new int[slots];
                _shown = new int[slots];
            }
            if (_summaryLogged)
            {
                return;
            }
            for (int i = 0; i < _loaded.Length; i++)
            {
                _loaded[i] = 0;
                _shown[i] = 0;
            }
        }

        /// <summary>
        /// The overlay's material, built once for the whole KSP session.
        ///
        /// Static and flagged HideAndDontSave so it survives a scene change: KSP runs
        /// Resources.UnloadUnusedAssets there, which would otherwise collect it, and rebuilding it per scene
        /// bought nothing.
        ///
        /// Destroying it per scene was TRIED as a fix for the ghost-shadow bug (see the README) and did NOT
        /// fix it. Kept on its own merits.
        /// </summary>
        private Material HighlightMaterial()
        {
            if (_material != null)
            {
                return _material;
            }

            for (int i = 0; i < ShaderCandidates.Length; i++)
            {
                Shader shader = Shader.Find(ShaderCandidates[i]);
                if (shader == null)
                {
                    continue;
                }

                _material = new Material(shader);
                _material.hideFlags = HideFlags.HideAndDontSave;

                // Which of these a shader exposes depends on which one we landed on, so set whatever is there
                // rather than assume. A textured shader with no texture would otherwise draw black.
                if (_material.HasProperty("_Color"))
                {
                    _material.SetColor("_Color", HighlightColor);
                }
                if (_material.HasProperty("_TintColor"))
                {
                    _material.SetColor("_TintColor", HighlightColor);
                }
                if (_material.HasProperty("_MainTex"))
                {
                    _material.SetTexture("_MainTex", Texture2D.whiteTexture);
                }
                _material.renderQueue = 3100;

                LOGGER.LogInfo($"Highlight material using shader '{ShaderCandidates[i]}'");
                return _material;
            }

            LOGGER.LogError("No usable shader found for the highlight — turning it off");
            Active = false;
            return null;
        }
    }
}
