using System;
using System.Collections.Generic;
using UnityEngine;
using com.github.lhervier.ksp.shared;
using com.github.lhervier.ksp.bulldozermod.diagnostics;
using com.github.lhervier.ksp.bulldozermod.parallax;
using com.github.lhervier.ksp.bulldozermod.persistence;
using com.github.lhervier.ksp.bulldozermod.terrain;

namespace com.github.lhervier.ksp.bulldozermod
{
    /// <summary>
    /// The bulldozer blade: clears the scatters (Parallax rocks and the like) along a blade pass, a strip
    /// running straight ahead of the vessel, as long and as wide as the player picks.
    ///
    /// A blade wears out. Every pass spends the mask cells it turns black, and a blade that runs out stops
    /// working — see <see cref="PassCost"/> for what that charges and what it does not. That is a
    /// performance guard wearing a gameplay costume: each cleared quad carries a PQSMod and a readable
    /// Texture2D for the whole session, all rebuilt at every load, so kilometers of cleared road would cost
    /// tens of megabytes replayed on every flight scene. "My blade is worn out" is the readable version of
    /// that.
    ///
    /// Click flow: work out the strip, check it is clearable AND affordable, charge the blade, record the
    /// pass (with the quads it covers) in the per-save ScenarioModule, save to a dedicated file, reload. The
    /// decals are injected by the scenario's OnLoad while the PQS is still cold. Nothing is applied live:
    /// the exclusion is decided when Parallax builds a quad's scatters, so it needs a fresh build to take
    /// effect.
    /// </summary>
    public class BulldozerCleanModule : PartModule
    {
        private static readonly ModLogger LOGGER = new ModLogger("CleanModule");

        // Both sliders step by one mask cell: finer would promise a precision the mask cannot express, since
        // a cell is the smallest thing it can clear.
        private const float Step = (float)SurfaceBand.WidthStep;
        private const float MinLength = Step;
        private const float MaxLength = 200f;

        // 20 m is the ceiling Lionel set. Worth knowing where it lands: the big scatters are CLUSTERS
        // removed by their anchor, whose extent is 15-25 m, so even at 20 m some boulders keep leaning into
        // the pass. Measured on the Mun, a 200 m pass took out 5-9% of the rocks around it at 5 m wide.
        //
        // The SAME ceiling on all three blades, deliberately: a blade is assumed to make several passes back
        // and forth, so a 2 m blade clearing a 20 m strip is not a lie. What separates the three models is
        // how much they can clear before wearing out, not how wide a strip they can cover.
        private const float MinWidth = Step;
        private const float MaxWidth = 20f;

        // Two cells rather than the minimum: at exactly one cell the strip sits ON the mask's resolution
        // limit, where the cleared corridor can shift half a cell off the blade and leave up to 12% of its
        // own track standing. A default should not start life on a known limit.
        private const float DefaultWidth = 2f * Step;

        // How high the cutting edge may ride over the ground, and how far the blade may lean out of the
        // ground plane, for a pass to be allowed. Together they are what makes the blade a tool that has to
        // be PLACED: bolted high on the hull, or pointing off at an angle, it no longer clears anything.
        //
        // Measured against the ground itself rather than the horizon (see BladeStance), so a rover working
        // a slope is not punished for following it.
        //
        // The band builder keeps its own, much looser 45 deg guard against the geoid. That one asks a
        // different question -- "is this axis a heading at all" -- and has to stay loose, because on a
        // slope even a well-mounted blade leans well away from the horizon.
        //
        // ⚠️ Both numbers are quoted to the player in the parts' own descriptions
        // (GameData/BulldozerMod/Localization/*.properties, blade*Description) — a copy that nothing keeps
        // in step. GetInfo() below reads them from here, the descriptions do not.
        private const float MaxClearance = 0.5f;
        private const float MaxTilt = 15f;

        // Dedicated reload save (NOT "persistent"): the reload is a mod manoeuvre, not a save the player
        // asked for, and a scene-exit autosave to "persistent" could clobber it. Same as KSP-ReRoot.
        private const string ReloadSaveName = "BulldozerReload";

        // How often the announced cost is recomputed while the PAW is open. It walks Parallax's quad
        // registry and rasterises the strip per quad, so it is not free — but the player needs it to follow
        // the rover and the sliders, which is what makes a pass fit before it is paid for.
        private const float CostRefreshInterval = 0.5f;

        /// <summary>
        /// Length (m) of the pass the overlays should announce.
        ///
        /// Static because the highlight and the quad overlay are scene-wide addons with no PAW of their own,
        /// while this value belongs to a part. It follows whichever PAW the player last touched; with
        /// several blades, each keeping its own persisted value, a fresh scene leaves whichever started
        /// last — and the first slider move settles it.
        /// </summary>
        public static double SelectedLength = 50.0;

        /// <summary>Width (m) of the pass the overlays should announce. Same caveat as
        /// <see cref="SelectedLength"/>.</summary>
        public static double SelectedWidth = DefaultWidth;

        /// <summary>The blade whose PAW was last touched — the one the overlays aim with. Same caveat as
        /// <see cref="SelectedLength"/>.</summary>
        public static BulldozerCleanModule Selected;

        /// <summary>Length (m) of the pass this blade would clear. Player setting, persisted per part.</summary>
        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = false,
                  guiName = "#LOC_BulldozerMod_passLength", guiUnits = "m", guiFormat = "F0")]
        [UI_FloatRange(minValue = MinLength, maxValue = MaxLength, stepIncrement = Step,
                       scene = UI_Scene.Flight)]
        public float passLength = 50f;

        /// <summary>Width (m) of the pass this blade would clear. Player setting, persisted per part.</summary>
        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = false,
                  guiName = "#LOC_BulldozerMod_passWidth", guiUnits = "m", guiFormat = "F0")]
        [UI_FloatRange(minValue = MinWidth, maxValue = MaxWidth, stepIncrement = Step,
                       scene = UI_Scene.Flight)]
        public float passWidth = DefaultWidth;

        /// <summary>How many mask cells this blade can clear over its whole life. Set per part in the cfg.</summary>
        [KSPField]
        public float cellCapacity = 200f;

        /// <summary>
        /// Cells this blade has left. Persisted per part, because wear belongs to the blade rather than to
        /// the save — a fresh blade brought along is a fresh quota.
        ///
        /// Negative means "never started": the cfg's capacity is what fills it on first start.
        /// </summary>
        [KSPField(isPersistant = true)]
        public float cellsLeft = -1f;

        /// <summary>
        /// Name of the model transform whose +Z points the way the blade faces, or empty to read the
        /// vessel's control point instead.
        ///
        /// This is the one thing a dedicated part settles for free: a blade knows which way it faces, so
        /// "ahead" no longer has to be guessed from whatever the player last picked as a control point. Left
        /// empty on the placeholder models, which carry no such transform.
        /// </summary>
        [KSPField]
        public string bladeTransform = "";

        /// <summary>Wear left, as the PAW shows it.</summary>
        [KSPField(guiActive = true, guiActiveEditor = true, guiName = "#LOC_BulldozerMod_bladeWear")]
        public string wearStatus = "";

        /// <summary>What the pass currently set up would cost, as the PAW shows it.</summary>
        [KSPField(guiActive = true, guiActiveEditor = false, guiName = "#LOC_BulldozerMod_passCost")]
        public string costStatus = "";

        /// <summary>How high the cutting edge rides over the ground, as the PAW shows it.</summary>
        [KSPField(guiActive = true, guiActiveEditor = false, guiName = "#LOC_BulldozerMod_bladeClearance")]
        public string clearanceStatus = "";

        /// <summary>How far the blade leans out of the ground plane, as the PAW shows it.</summary>
        [KSPField(guiActive = true, guiActiveEditor = false, guiName = "#LOC_BulldozerMod_bladeTilt")]
        public string tiltStatus = "";

        private Transform _bladeFace;
        private bool _bladeFaceResolved;
        private Vector3 _localEdge;
        private bool _edgeResolved;
        private bool _hasEdge;
        private float _costTimer;

        /// <summary>
        /// The strip a pass of <paramref name="length"/> by <paramref name="width"/> meters would clear right
        /// now: from the vessel, straight ahead. Returns false when the vessel points nowhere usable.
        ///
        /// Shared with the overlays on purpose — they must aim at the very strip a click would apply.
        /// </summary>
        public static bool TryBuildBand(Vessel vessel, double length, double width, out SurfaceBand band)
        {
            band = new SurfaceBand();
            if (vessel == null || vessel.mainBody == null)
            {
                return false;
            }

            CelestialBody body = vessel.mainBody;

            // The origin goes through lat/lon because that is what the action stores: the strip replayed at
            // load then starts exactly where this one does.
            Vector3d origin = body.GetRelSurfaceNVector(vessel.latitude, vessel.longitude);

            Vector3d nose;
            if (!TryGetNose(vessel, out nose))
            {
                return false;
            }

            double vertical = Vector3d.Dot(nose, origin);
            Vector3d heading = nose - origin * vertical;

            // The aiming axis has to be MORE HORIZONTAL THAN VERTICAL, i.e. within 45 deg of the horizon.
            // A small threshold does not work, and that is not theoretical: on a slope, an axis aimed at the
            // SKY still leaves a sizeable horizontal residue — the direction the deck happens to lean, which
            // is not a heading at all. Measured in game on the Mun with a Mk2 lander can: its "Up" and
            // "Reversed" control points (orientation 0,0,180, so nose down) both passed a sin(10 deg) test
            // and sent a 200 m pass off in an arbitrary direction — the two being exactly 180 deg apart,
            // which is the giveaway that both were reading the same tilt.
            if (heading.magnitude <= Math.Abs(vertical))
            {
                return false;
            }

            return SurfaceBand.TryCreate(body.Radius, origin, heading, length, width, out band);
        }

        [KSPEvent(guiActive = true, guiActiveEditor = false, active = true,
                  guiName = "#LOC_BulldozerMod_clearScatters")]
        public void ClearScatters()
        {
            try
            {
                RecordAndReload();
            }
            catch (Exception e)
            {
                LOGGER.LogError("Clear failed: " + e);
                Warn(ModLocalization.GetString("clearFailed"));
            }
        }

        /// <summary>
        /// Shows, in place, which rocks a pass would remove — and stops on the next click.
        ///
        /// Not a convenience: at the narrowest width the strip sits at the resolution limit of the mask, so
        /// what a pass really clears depends on where the cells fall. This is what makes that visible, along
        /// with the clusters a narrow pass cannot take out.
        /// </summary>
        [KSPEvent(guiActive = true, guiActiveEditor = false, active = true,
                  guiName = "#LOC_BulldozerMod_highlightOn")]
        public void ToggleHighlight()
        {
            ScatterHighlighter.Active = !ScatterHighlighter.Active;
            SyncHighlightLabel();
        }

        /// <summary>
        /// Dumps to KSP.log what the pass would decide, rock by rock, without changing anything.
        ///
        /// Test tool: it is what separates "the mask is in the wrong place" from "the blade is 5 m wide and
        /// those rocks are clusters anchored elsewhere". To be removed with the rest of Src/diagnostics.
        /// </summary>
        [KSPEvent(guiActive = true, guiActiveEditor = false, active = true,
                  guiName = "#LOC_BulldozerMod_dumpPass")]
        public void DumpPass()
        {
            try
            {
                BandDiagnostic.Dump(FlightGlobals.ActiveVessel, passLength, passWidth);
                Warn(ModLocalization.GetString("dumpDone"));
            }
            catch (Exception e)
            {
                LOGGER.LogError("Dump failed: " + e);
            }
        }

        /// <summary>
        /// What the editor's part tooltip says about this module.
        ///
        /// Everything numeric here is read from the constants above, so the tooltip cannot drift away from
        /// what the module actually enforces. The parts' descriptions repeat the two placement limits in
        /// prose, and those DO have to be edited by hand.
        /// </summary>
        public override string GetInfo()
        {
            return ModLocalization.GetString("moduleInfo",
                                             Mathf.RoundToInt(cellCapacity),
                                             MinLength.ToString("F0"), MaxLength.ToString("F0"),
                                             MinWidth.ToString("F0"), MaxWidth.ToString("F0"),
                                             MaxClearance.ToString("F2"), MaxTilt.ToString("F0"));
        }

        public override string GetModuleDisplayName()
        {
            return ModLocalization.GetString("moduleTitle");
        }

        public override void OnStart(StartState state)
        {
            base.OnStart(state);

            // A blade straight out of the VAB is a full one. Done here rather than in the field initialiser
            // because the capacity comes from the cfg, which is only merged in by the time OnStart runs.
            if (cellsLeft < 0f)
            {
                cellsLeft = cellCapacity;
            }

            // Nothing to clear without Parallax: hide the entries rather than offer a no-op.
            if (!ParallaxInterop.Available)
            {
                Events["ClearScatters"].guiActive = false;
                Events["ToggleHighlight"].guiActive = false;
                Events["DumpPass"].guiActive = false;
                Fields["passLength"].guiActive = false;
                Fields["passWidth"].guiActive = false;
                Fields["wearStatus"].guiActive = false;
                Fields["wearStatus"].guiActiveEditor = false;
                Fields["costStatus"].guiActive = false;
                Fields["clearanceStatus"].guiActive = false;
                Fields["tiltStatus"].guiActive = false;
                return;
            }

            PublishPass();
            BaseField length = Fields["passLength"];
            if (length.uiControlFlight != null)
            {
                length.uiControlFlight.onFieldChanged = OnPassChanged;
            }
            BaseField width = Fields["passWidth"];
            if (width.uiControlFlight != null)
            {
                width.uiControlFlight.onFieldChanged = OnPassChanged;
            }

            SyncWear();

            // The flag outlives the scene, so the label has to be re-derived rather than assumed off.
            SyncHighlightLabel();
        }

        public override void OnUpdate()
        {
            base.OnUpdate();
            if (!ParallaxInterop.Available || cellsLeft <= 0f)
            {
                return;
            }

            // Only while somebody is reading it: the cost is the one thing here that walks the whole quad
            // registry every time it is refreshed.
            if (part.PartActionWindow == null)
            {
                return;
            }

            // Every frame, unlike the cost: it is one raycast, and the player reads it while working a
            // robotic hinge — a value refreshed twice a second is not something you can aim with.
            SyncStance();

            _costTimer -= Time.deltaTime;
            if (_costTimer > 0f)
            {
                return;
            }
            _costTimer = CostRefreshInterval;
            SyncCost();
        }

        // Which way the pass runs. A blade part answers that itself, through a transform of its own model.
        //
        // Falls back to the navball's nose — the control point's own +up axis (NavBall targets
        // ActiveVessel.ReferenceTransform, and ModuleCommand draws its "forward" arrow along
        // controlPoint.transform.up) — when no blade has declared one. So "control from here" aims the pass
        // exactly as it aims the rover, and the player reads the heading off an instrument already on screen.
        private static bool TryGetNose(Vessel vessel, out Vector3d nose)
        {
            nose = Vector3d.zero;
            CelestialBody body = vessel.mainBody;

            BulldozerCleanModule blade = Selected;
            if (blade != null && blade.vessel == vessel)
            {
                Transform face = blade.BladeFace;
                if (face != null)
                {
                    nose = body.GetRelSurfaceDirection(face.forward);
                    return true;
                }
            }

            if (vessel.ReferenceTransform == null)
            {
                return false;
            }
            nose = body.GetRelSurfaceDirection(vessel.ReferenceTransform.up);
            return true;
        }

        // The model transform this blade faces along, or null when it declares none. Resolved once: the
        // lookup walks the part's whole hierarchy, and this is read every frame the overlays are on.
        private Transform BladeFace
        {
            get
            {
                if (!_bladeFaceResolved)
                {
                    _bladeFaceResolved = true;
                    if (!string.IsNullOrEmpty(bladeTransform))
                    {
                        _bladeFace = part.FindModelTransform(bladeTransform);
                        if (_bladeFace == null)
                        {
                            LOGGER.LogWarning($"Blade transform '{bladeTransform}' not found on " +
                                              $"'{part.partInfo?.name}' — falling back to the control point");
                        }
                    }
                }
                return _bladeFace;
            }
        }

        // Bound to the module whose slider moved, which is the point: it publishes THAT part's values, so the
        // overlays follow the PAW the player is actually using.
        private void OnPassChanged(BaseField field, object oldValue)
        {
            PublishPass();
            SyncCost();
        }

        private void PublishPass()
        {
            SelectedLength = passLength;
            SelectedWidth = passWidth;
            Selected = this;
        }

        // A toggle's label must announce what the NEXT click does, not the current state.
        private void SyncHighlightLabel()
        {
            Events["ToggleHighlight"].guiName =
                ModLocalization.GetString(ScatterHighlighter.Active ? "highlightOff" : "highlightOn");
        }

        // Wear left, and whether the blade can still be used at all.
        private void SyncWear()
        {
            wearStatus = ModLocalization.GetString("bladeWearValue",
                                                   Mathf.RoundToInt(cellsLeft),
                                                   Mathf.RoundToInt(cellCapacity));

            bool worn = cellsLeft <= 0f;
            Events["ClearScatters"].guiName = ModLocalization.GetString(worn ? "bladeWorn" : "clearScatters");
            Events["ClearScatters"].guiActive = !worn;
            Fields["costStatus"].guiActive = !worn;
            Fields["clearanceStatus"].guiActive = !worn;
            Fields["tiltStatus"].guiActive = !worn;
        }

        // What the pass currently set up would cost. Silent about anything it cannot answer: an unusable
        // heading already has its own refusal message at click time.
        private void SyncCost()
        {
            int cost;
            costStatus = TryEstimateCost(FlightGlobals.ActiveVessel, out cost)
                ? ModLocalization.GetString("passCostValue", cost)
                : ModLocalization.GetString("passCostUnknown");
        }

        // Cells the pass would newly clear, or false when the strip cannot be worked out at all.
        private bool TryEstimateCost(Vessel vessel, out int cost)
        {
            cost = 0;
            if (vessel == null || vessel.mainBody == null || vessel.mainBody.pqsController == null)
            {
                return false;
            }

            SurfaceBand band;
            if (!TryBuildBand(vessel, passLength, passWidth, out band))
            {
                return false;
            }

            cost = PassCost.Compute(vessel.mainBody, band,
                                    QuadLocator.FindImpactedQuads(vessel.mainBody, band));
            return true;
        }

        // How the blade lies over the ground, as the PAW shows it. Both values name their limit as soon as
        // they break it: the player is holding a hinge control and needs to know which way to move it, not
        // just that the button has gone quiet.
        private void SyncStance()
        {
            BladeStance stance;
            if (!TryMeasureStance(out stance))
            {
                clearanceStatus = ModLocalization.GetString("bladeGroundUnknown");
                tiltStatus = ModLocalization.GetString("bladeGroundUnknown");
                return;
            }

            clearanceStatus = stance.clearance > MaxClearance
                ? ModLocalization.GetString("bladeClearanceTooHigh",
                                            stance.clearance.ToString("F2"), MaxClearance.ToString("F2"))
                : ModLocalization.GetString("bladeClearanceValue", stance.clearance.ToString("F2"));

            tiltStatus = stance.tilt > MaxTilt
                ? ModLocalization.GetString("bladeTiltTooSteep",
                                           stance.tilt.ToString("F0"), MaxTilt.ToString("F0"))
                : ModLocalization.GetString("bladeTiltValue", stance.tilt.ToString("F0"));
        }

        // Where the blade is with respect to the ground, or false when there is none under it.
        private bool TryMeasureStance(out BladeStance stance)
        {
            stance = new BladeStance();
            if (vessel == null || vessel.mainBody == null || !TryResolveLocalEdge())
            {
                return false;
            }
            return BladeStance.TryMeasure(vessel.mainBody,
                                          part.transform.TransformPoint(_localEdge),
                                          BladeUp,
                                          out stance);
        }

        // The blade's own up axis: what the ground normal is compared against. Comes from the same model
        // transform as the facing axis, which carries the part's orientation unrotated.
        private Vector3 BladeUp
        {
            get
            {
                Transform face = BladeFace;
                return face != null ? face.up : part.transform.up;
            }
        }

        // The point the ground clearance is measured at, in part space: the middle of the cutting edge, i.e.
        // the corner of the model's box that is at once the lowest and the furthest forward. That corner IS
        // the lip -- the model is authored with +Y up and +Z facing, and the mouldboard's cutting edge is
        // both the bottom of the part and its front.
        //
        // Derived from the meshes rather than declared in the cfg, so it follows the three widths, and any
        // later change to the generator's EDGE_DROP, without a second description of the shape to keep in
        // step. Resolved once: it walks every mesh in the part.
        private bool TryResolveLocalEdge()
        {
            if (_edgeResolved)
            {
                return _hasEdge;
            }
            _edgeResolved = true;

            Transform origin = part.transform;
            bool any = false;
            Vector3 min = Vector3.zero;
            Vector3 max = Vector3.zero;
            foreach (MeshFilter filter in part.FindModelComponents<MeshFilter>())
            {
                Mesh mesh = filter.sharedMesh;
                if (mesh == null)
                {
                    continue;
                }

                // Corner by corner rather than through Renderer.bounds: the latter is axis-aligned in WORLD
                // space, which says nothing about where the lip is on the part.
                Bounds box = mesh.bounds;
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 local = new Vector3((corner & 1) == 0 ? box.min.x : box.max.x,
                                                (corner & 2) == 0 ? box.min.y : box.max.y,
                                                (corner & 4) == 0 ? box.min.z : box.max.z);
                    Vector3 p = origin.InverseTransformPoint(filter.transform.TransformPoint(local));
                    if (!any)
                    {
                        min = max = p;
                        any = true;
                    }
                    else
                    {
                        min = Vector3.Min(min, p);
                        max = Vector3.Max(max, p);
                    }
                }
            }

            if (!any)
            {
                LOGGER.LogWarning($"No mesh on '{part.partInfo?.name}' — cannot tell how high its blade " +
                                  "rides, so ground checks are off for it");
                return false;
            }

            _localEdge = new Vector3(0.5f * (min.x + max.x), min.y, max.z);
            _hasEdge = true;
            return true;
        }

        private void RecordAndReload()
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null || vessel.mainBody == null || vessel.mainBody.pqsController == null)
            {
                LOGGER.LogError("No active vessel / body / PQS");
                return;
            }
            if (BulldozerScenario.Instance == null)
            {
                LOGGER.LogError("Scenario not available; cannot record action");
                return;
            }

            CelestialBody body = vessel.mainBody;

            SurfaceBand band;
            if (!TryBuildBand(vessel, passLength, passWidth, out band))
            {
                LOGGER.LogWarning("The blade points nowhere usable — refusing the pass");
                Warn(ModLocalization.GetString("headingUnusable"));
                return;
            }

            // Placement is checked here rather than at slider time: it is the one condition the player is
            // actively working on with a hinge, so it has to be read at the instant of the click.
            BladeStance stance;
            if (!TryMeasureStance(out stance))
            {
                LOGGER.LogWarning("No ground under the blade — refusing the pass");
                Warn(ModLocalization.GetString("bladeNoGround"));
                return;
            }
            if (stance.clearance > MaxClearance)
            {
                LOGGER.LogInfo($"Blade rides {stance.clearance:F2} m over the ground " +
                               $"(max {MaxClearance:F2}) — refusing");
                Warn(ModLocalization.GetString("bladeTooHigh",
                                               stance.clearance.ToString("F2"), MaxClearance.ToString("F2")));
                return;
            }
            if (stance.tilt > MaxTilt)
            {
                LOGGER.LogInfo($"Blade leans {stance.tilt:F0} deg off the ground (max {MaxTilt:F0}) — refusing");
                Warn(ModLocalization.GetString("bladeTooSteep",
                                               stance.tilt.ToString("F0"), MaxTilt.ToString("F0")));
                return;
            }

            // The quads are captured HERE, in flight: at load time the terrain is not built yet and their
            // geometry cannot be recovered.
            List<QuadRef> quads = QuadLocator.FindImpactedQuads(body, band);
            if (quads.Count == 0)
            {
                LOGGER.LogError("No leaf quad found under the pass — is the terrain fully built?");
                Warn(ModLocalization.GetString("clearFailed"));
                return;
            }

            // Refuse now rather than after a pointless reload: a quad already claimed by another mod's decal
            // can never take ours (Parallax allows one decal per quad).
            foreach (QuadRef quad in quads)
            {
                if (quad.owner != null && !(quad.owner is BulldozerScatterDecal))
                {
                    LOGGER.LogWarning($"Quad at {quad.direction} already owned by " +
                                      $"'{quad.owner.gameObject.name}' — refusing");
                    Warn(ModLocalization.GetString("areaReserved", quad.owner.gameObject.name));
                    return;
                }
            }

            int cost = PassCost.Compute(body, band, quads);
            if (cost <= 0)
            {
                // Everything under the strip is already cleared. Nothing to record, and above all nothing
                // worth a reload — which is the expensive part of a pass, not the blade.
                LOGGER.LogInfo("Pass would blacken no new cell — nothing to do");
                Warn(ModLocalization.GetString("nothingToClear"));
                return;
            }
            if (cost > cellsLeft)
            {
                LOGGER.LogInfo($"Pass costs {cost} cell(s) but the blade has {cellsLeft:F0} left — refusing");
                Warn(ModLocalization.GetString("notEnoughWear", cost, Mathf.RoundToInt(cellsLeft)));
                return;
            }

            // Charged BEFORE the game is snapshotted below, or the reload would bring back a blade that
            // never paid for this pass.
            cellsLeft -= cost;
            SyncWear();

            BulldozerAction action = new BulldozerAction
            {
                bodyName = body.name,
                lat = vessel.latitude,
                lon = vessel.longitude,
                forward = band.forward,
                length = band.length,
                width = band.width,
                maxLevel = body.pqsController.maxLevel
            };
            foreach (QuadRef quad in quads)
            {
                action.quads.Add(new BulldozerAction.ImpactedQuad
                {
                    direction = quad.direction,
                    halfDiagonal = quad.halfDiagonal
                });
            }
            BulldozerScenario.Instance.AddAction(action);
            LOGGER.LogInfo($"Recorded pass on {body.name} lat={action.lat:F5} lon={action.lon:F5} " +
                           $"length={action.length:F0}m width={action.width:F0}m " +
                           $"over {action.quads.Count} quad(s), {cost} cell(s) charged, " +
                           $"{cellsLeft:F0} left on the blade");

            // The new action is serialized through the scenario's OnSave. Nothing else is patched: the vessel
            // keeps its saved pose, and since no terrain height changes it reappears exactly where it was.
            int focusIdx = FlightGlobals.Vessels.IndexOf(vessel);
            Game game = HighLogic.CurrentGame.Updated();
            GamePersistence.SaveGame(game, ReloadSaveName, HighLogic.SaveFolder, SaveMode.OVERWRITE);
            FlightDriver.StartAndFocusVessel(ReloadSaveName, focusIdx);
        }

        private static void Warn(string message)
        {
            ScreenMessages.PostScreenMessage(message, 5f, ScreenMessageStyle.UPPER_CENTER);
        }
    }
}
