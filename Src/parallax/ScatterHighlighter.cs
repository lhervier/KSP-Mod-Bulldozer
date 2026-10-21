using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Parallax;
using Parallax.PQS_Mods;
using com.github.lhervier.ksp.shared;
using com.github.lhervier.ksp.mod.bulldozer.terrain;

namespace com.github.lhervier.ksp.mod.bulldozer.parallax
{
    /// <summary>
    /// Draws, every frame, an overlay over the collideable scatters that a blade pass would remove right
    /// now. Toggled from the PAW; nothing is drawn while it is off.
    ///
    /// The verdict is not a geometric "is the rock inside the rectangle": it is the mask itself. For every
    /// quad in reach the overlay builds the raster the injection would build (ScatterCleaner.RasterFor) and
    /// asks it the same question the compute shader will ask - bilinear sample, blocked under 0.5. At the
    /// narrowest width the strip sits at the resolution limit of the mask, so a geometric answer would be
    /// wrong exactly where the player needs it right: it would promise a clean corridor where the cells give
    /// a wider one, a stepped one at a quad seam, or none at all on a quad another mod already owns.
    ///
    /// Only collideable scatters can be shown, and that is the useful set anyway: they are the only ones
    /// Parallax describes instance by instance, and the only ones a rover can hit. The small families exist
    /// solely as GPU instances — a clean wipes them too, but there is nothing to point at.
    ///
    /// The unit shown is the scatter INSTANCE, which for the big families is a whole CLUSTER of boulders.
    /// That is deliberate: it is exactly the unit the block map removes or keeps. A cluster anchored outside
    /// the strip keeps every one of its boulders, including the ones reaching into it — which a narrow
    /// pass runs into constantly, and what this overlay is there to show rather than let the player discover
    /// it after a reload (see the cluster PIEGE in CLAUDE.md).
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
        /// The blade whose pass is being previewed, or null when no preview is running.
        ///
        /// ONE at a time, and that is a cost decision rather than a UI one: a preview walks Parallax's quad
        /// registry, rasterises the strip into every quad it touches and tests every loaded instance against
        /// the resulting mask, on every frame. Two blades would double all of it. So turning one on turns
        /// the others off, and each PAW re-derives its own label from this field.
        ///
        /// It doubles as the aim: the blade being previewed is the one the overlays follow, with ITS own
        /// length and width. Nothing else needs to track "the blade the player is working with".
        ///
        /// Static because this addon is destroyed and rebuilt at every scene change, while the choice
        /// belongs to the player — but a blade does not survive a scene change, so the preview goes out on
        /// its own when one happens, rather than pointing at a destroyed part.
        /// </summary>
        public static BulldozerCleanModule Source;

        // How far the overlay is pulled towards the camera, as a fraction of each vertex's own distance to
        // it, to keep it clear of the rock's own depth. Proportional on purpose: depth precision degrades
        // with distance, so a fixed offset that works up close stops working further out.
        //
        // Applied as a scaling CENTRED ON THE CAMERA, never as a translation from the instance's anchor:
        // see the DrawMesh call.
        private const float DepthPull = 0.002f;

        private static readonly Color HighlightColor = new Color(1f, 0.15f, 0.1f, 0.6f);

        // Static, and never destroyed: see HighlightMaterial().
        private static Material _material;

        // The other half of the preview: the strip itself, painted on the ground. Owned here rather
        // than run as an addon of its own so both halves share one switch, one camera and one set of
        // scene guards - and so the trace is drawn from the very band the highlight is aiming with.
        private readonly BandTrace _trace = new BandTrace();


        // The activation summary is logged on the first frame that actually finds something: on the very
        // first frame Parallax has usually not filled its lists yet and everything reads zero.
        private bool _summaryLogged;
        private bool _warnedNoRegistry;
        private int _lastShown = -1;
        private int _lastTotal = -1;

        // Rate limit of the per-frame count line below.
        private const float CountLogPeriod = 1f;
        private float _nextCountLog;
        private int[] _loaded;
        private int[] _shown;

        // Reused every frame: measuring a quad then allocates nothing, which matters in a loop that walks
        // every loaded quad.
        private readonly Vector3d[] _corners = new Vector3d[4];

        private void Update()
        {
            BulldozerCleanModule blade = Source;
            if (blade == null)
            {
                _summaryLogged = false;
                _warnedNoRegistry = false;
                _lastShown = -1;
                _lastTotal = -1;
                _nextCountLog = 0f;
                _trace.Dispose();
                return;
            }

            // Draw ONLY on a settled flight scene: the blade outlives the frame it was picked in, and we
            // would otherwise keep drawing all through a reload, while quads and their GPU buffers are being
            // torn down and rebuilt — pointless at best.
            //
            // Was TRIED as a fix for the ghost-shadow bug (see CLAUDE.md) and did NOT fix it. Kept on its
            // own merits.
            if (!HighLogic.LoadedSceneIsFlight || !FlightGlobals.ready)
            {
                return;
            }

            // The blade's own vessel, not the active one: a preview started from a rover parked next door
            // must show that rover's strip. A blade whose part is gone (destroyed, unloaded) puts the
            // preview out rather than drawing nowhere.
            Vessel vessel = blade.part == null ? null : blade.vessel;
            if (vessel == null || vessel.mainBody == null)
            {
                Source = null;
                return;
            }
            if (vessel.packed)
            {
                return;
            }

            // ONE camera, never null. `camera: null` submits the draw to EVERY camera in the game, and an
            // overlay only makes sense in the one being looked at; Parallax names Camera.main explicitly for
            // its own scatters.
            //
            // Was TRIED as a fix for the ghost-shadow bug (see CLAUDE.md) and did NOT fix it. Kept on its
            // own merits.
            Camera camera = Camera.main;
            if (camera == null)
            {
                return;
            }

            CelestialBody body = vessel.mainBody;

            // The strip a click would apply right now, built by the very code the click uses, off the very
            // blade that would apply it.
            SurfaceBand band;
            if (!BulldozerCleanModule.TryBuildBand(blade, blade.passLength, blade.passWidth, out band))
            {
                return;
            }

            // The strip on the ground, drawn before anything else because it does not need
            // Parallax to have loaded a single rock: it is what shows the far end of a long
            // pass, where there is nothing to highlight either way.
            _trace.Draw(body, band, camera);

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

            Vector3d originWorld = blade.OriginPoint;

            // The same for every instance this frame: see the DrawMesh call.
            Vector3 cameraPosition = camera.transform.position;
            Matrix4x4 towardCamera = Matrix4x4.Translate(cameraPosition)
                                     * Matrix4x4.Scale(Vector3.one * (1f - DepthPull))
                                     * Matrix4x4.Translate(-cameraPosition);

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
                // were actually in range. A quad matters only if it comes within reach of the strip plus its
                // own width; the strip never runs further than its length from the blade's edge, which is
                // where it starts.
                double quadReach = band.length + band.width + Math.Sqrt(quadData.sqrQuadWidth);
                if ((((Vector3d)quadData.quad.gameObject.transform.position) - originWorld).sqrMagnitude
                    > quadReach * quadReach)
                {
                    continue;
                }

                // A quad another mod's decal already owns can never take ours, so nothing on it would go.
                // Silently, which is exactly why it has to show here (near the KSC, ParallaxMaskKSC).
                PQSMod_MapDecalVertexRemoveScatter quadOwner;
                if (PQSMod_MapDecalVertexRemoveScatter.maskedScattersPerQuad.TryGetValue(
                        quadData.quad, out quadOwner)
                    && !(quadOwner is BulldozerScatterDecal))
                {
                    continue;
                }

                // The mask this quad WOULD get, cell grid and all. Rebuilt per entry rather than memoised per
                // quad: Parallax keeps one entry per (quad, scatter family), so this repeats a handful of
                // times per quad — four Acos and a bounded cell scan each, against the 59 000 per-instance
                // tests the rejection above is there to avoid. Worth revisiting only if it shows up in a
                // profile on a small body, where a long pass crosses many quads.
                double halfDiagonal;
                if (!QuadLocator.TryGetHalfDiagonal(body, quadData.quad, _corners, out halfDiagonal))
                {
                    continue;
                }
                BandRaster raster = ScatterCleaner.RasterFor(
                    body, quadData.quad.positionPlanetRelative, halfDiagonal, band);
                if (!raster.ClearsAnyCell())
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
                // draw instances it never instantiated: the collider is a child of the quad's GameObject,
                // posed in the quad's LOCAL frame (position, scale, and since Parallax 1.0.5 the rotation).
                Transform quadTransform = quadData.quad.gameObject.transform;
                Matrix4x4 quadToWorld = quadTransform.localToWorldMatrix;
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

                    // The instance's own direction in body frame - the very quantity the compute shader
                    // projects into the block map (its avgDirection), so the answer below is the shader's.
                    Vector3d rockDir = body.GetRelSurfacePosition(world).normalized;
                    if (!raster.Blocks(rockDir))
                    {
                        continue;
                    }

                    // Built in the quad's frame, as Parallax does since 1.0.5. The world-frame product it used
                    // before gives another twist around the normal, and on a cluster that twist swings every
                    // boulder around the anchor: the veil landed metres beside the rocks it marks.
                    Vector3 normal = alignToTerrain ? quadData.GetTerrainNormal(position.index) : planetNormal;
                    Quaternion localRotation = RotationFromUp(quadTransform.InverseTransformDirection(normal))
                                               * Quaternion.AngleAxis(position.rotation, Vector3.up);
                    Matrix4x4 instanceToWorld = quadToWorld
                                                * Matrix4x4.TRS(position.localPos, localRotation, position.localScale);
                    // Shadows OFF, and this is not cosmetic: the short DrawMesh overload defaults to
                    // castShadows = true, so the overlay threw its own shadow onto the ground — thin black
                    // streaks next to each rock, sliding around in 3D as the sun and camera moved.
                    // Drawn at the rock's own pose, then pulled towards the camera. The pull is needed: at
                    // exactly the rock's pose the overlay z-fights it and flickers, because Parallax builds
                    // its instance matrices on the GPU and ours on the CPU - the depths agree to within float
                    // noise, not exactly.
                    //
                    // The big families are clusters - one mesh whose pivot is the anchor, boulders spread
                    // 15-25 m around it - which rules out the two obvious pulls:
                    // - puffing the overlay up about its pivot pushes every boulder RADIALLY away from the
                    //   anchor (60 cm at 20 m for a factor of 1.03): the veil lands beside the rock;
                    // - translating it towards the camera from the anchor moves a boulder close to the lens
                    //   almost sideways when its anchor is metres away: it gains no depth, and the veil
                    //   came and went in patches as the camera turned (2026-10-21).
                    // A scaling centred on the camera moves every vertex along its own line of sight, by a
                    // fraction of its own distance: nothing moves on screen, everything gains depth.
                    Graphics.DrawMesh(
                        mesh,
                        towardCamera * instanceToWorld,
                        material, BulldozerConstants.TerrainLayer, camera, 0, null, ShadowCastingMode.Off, false);

                    shown++;
                    _shown[slot]++;
                }
            }

            if (!_summaryLogged && total > 0)
            {
                LogSummary(body, blade, shown, total);
                _summaryLogged = true;
            }
            else if ((shown != _lastShown || total != _lastTotal) && Time.unscaledTime >= _nextCountLog)
            {
                // This is what tells "nothing is under the strip" (total > 0) apart from "Parallax has
                // loaded nothing around here at all" (total == 0) — the two look identical on screen.
                //
                // Trace, and at most once a second. `total` counts the instances walked, which depends on
                // the vessel's position: while driving it changes on nearly EVERY frame, so an on-change
                // test alone left a Debug.Log — stack trace captured, line written to KSP.log — in the
                // per-frame path of the mod's most expensive mode. The change test is kept on top of the
                // throttle so a parked vessel stays silent.
                LOGGER.LogTrace($"Highlighting {shown} of {total} collideable instance(s)");
                _nextCountLog = Time.unscaledTime + CountLogPeriod;
            }
            _lastShown = shown;
            _lastTotal = total;
        }

        private void OnDestroy()
        {
            _trace.Dispose();
        }

        // One breakdown per activation. It answers the two questions this overlay always raises: how much
        // Parallax has actually loaded around the vessel, and WHICH families it can never show — the
        // non-collideable ones are wiped by a clean just the same, they simply have no per-instance data.
        private void LogSummary(CelestialBody body, BulldozerCleanModule blade, int shown, int total)
        {
            LOGGER.LogInfo($"Highlighting {shown} of {total} collideable instance(s) loaded around the " +
                           $"vessel, for a {blade.passLength:F0} x {blade.passWidth:F0} m pass " +
                           $"by '{blade.part.partInfo?.title}'");

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

            // Everything a pass removes, minus what we can draw: the difference is what the player will see
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
                    LOGGER.LogInfo($"    {name}: not collideable — removed by a pass, never highlighted");
                }
            }
        }

        // The rotation taking Vector3.up onto the unit vector normal. A copy of
        // CollisionManager.RotationFromUp, which is private: the overlay must twist exactly as the rock does.
        private static Quaternion RotationFromUp(Vector3 normal)
        {
            Vector3 v = Vector3.Cross(Vector3.up, normal);
            float c = normal.y;

            // 1 + cos, computed without cancellation when the normal points nearly down.
            float onePlusC = c >= 0 ? 1 + c : v.sqrMagnitude / (1 - c);
            if (onePlusC <= 0)
            {
                return new Quaternion(1, 0, 0, 0);
            }
            return Quaternion.Normalize(new Quaternion(v.x, v.y, v.z, onePlusC));
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
        /// Static and never destroyed so it survives a scene change: KSP runs Resources.UnloadUnusedAssets
        /// there, which would otherwise collect it, and rebuilding it per scene bought nothing.
        ///
        /// Destroying it per scene was TRIED as a fix for the ghost-shadow bug (see CLAUDE.md) and did NOT
        /// fix it. Kept on its own merits.
        /// </summary>
        private Material HighlightMaterial()
        {
            if (_material != null)
            {
                return _material;
            }

            _material = OverlayMaterial.Create(HighlightColor, "Rock highlight");
            if (_material == null)
            {
                // Nothing can be drawn at all, so stop pretending the overlay is on.
                Source = null;
            }
            return _material;
        }
    }
}
