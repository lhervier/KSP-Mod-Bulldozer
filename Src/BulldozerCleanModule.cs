using System;
using System.Collections.Generic;
using UnityEngine;
using Parallax.PQS_Mods;
using com.github.lhervier.ksp.shared;
using com.github.lhervier.ksp.bulldozermod.diagnostics;
using com.github.lhervier.ksp.bulldozermod.parallax;
using com.github.lhervier.ksp.bulldozermod.persistence;
using com.github.lhervier.ksp.bulldozermod.terrain;

namespace com.github.lhervier.ksp.bulldozermod
{
    /// <summary>
    /// The bulldozer blade: clears the scatters (Parallax rocks and the like) along a blade pass, a strip
    /// running straight ahead of the BLADE -- from its cutting edge, along the way it faces -- as long and
    /// as wide as the player picks. A rover can carry several, each aiming and wearing on its own.
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
        private bool _highlightLabelOn;
        private bool _reserved;

        // What the two stance lines were last written with, in the units they are FORMATTED in — hundredths
        // of a metre, whole degrees — plus whether each was over its limit, since that picks a different
        // sentence for the same number. NoStance means nothing has been written yet, and doubles as the
        // "no ground under the blade" state.
        //
        // They exist so the lines are only rebuilt when what they say changes: SyncStance runs every frame
        // (see OnUpdate for why), and each label costs a params array, an interpolated key and a
        // String.Format for a value that moves by the centimetre.
        private const int NoStance = int.MinValue;
        private int _shownClearanceCm = NoStance;
        private int _shownTiltDeg = NoStance;
        private bool _shownClearanceOver;
        private bool _shownTiltOver;

        /// <summary>
        /// The strip a pass of <paramref name="length"/> by <paramref name="width"/> meters would clear if
        /// <paramref name="blade"/> were used right now: from that blade's own cutting edge, along the way
        /// it faces. Returns false when the blade points nowhere usable.
        ///
        /// The strip is <see cref="SurfaceBand.GridSlack"/> shorter and narrower than what is asked for, so
        /// that a carefully placed pass can land on the cell count it advertises.
        ///
        /// A blade aims with ITSELF, and it is the only way a strip is ever built — the overlays call this
        /// too, with the blade being previewed. That matters as soon as a rover carries two: a rear-mounted
        /// blade has to send its pass backwards, and a blade slung out on an arm has to clear the ground it
        /// is actually over.
        /// </summary>
        public static bool TryBuildBand(BulldozerCleanModule blade, double length, double width,
                                        out SurfaceBand band)
        {
            band = new SurfaceBand();
            if (blade == null || blade.vessel == null || blade.vessel.mainBody == null)
            {
                return false;
            }

            // The ONE place the slack is taken off, because this is the one place a player's gesture becomes
            // a strip — the click, the price, the preview and the diagnostic all come through here. It must
            // not move into SurfaceBand.TryCreate: an action records the strip that was drawn, so the replay
            // path (BulldozerAction.ToBand) goes through TryCreate too, and would shave another metre off
            // every recorded zone on every single load.
            //
            // Length is taken off the FAR END — the origin is the cutting edge, which is the point the
            // player aims — while the width, being centred on that axis, loses half of it on each side.
            length -= SurfaceBand.GridSlack;
            width -= SurfaceBand.GridSlack;

            CelestialBody body = blade.vessel.mainBody;

            Vector3 facing = blade.FacingAxis;
            if (facing == Vector3.zero)
            {
                return false;
            }

            double lat, lon;
            blade.GetOrigin(out lat, out lon);

            return TryBuildBand(body, lat, lon, body.GetRelSurfaceDirection(facing), length, width, out band);
        }

        // The strip itself, once the point it starts from and the axis it runs along are settled.
        private static bool TryBuildBand(CelestialBody body, double lat, double lon, Vector3d nose,
                                         double length, double width, out SurfaceBand band)
        {
            band = new SurfaceBand();

            // The origin goes through lat/lon because that is what the action stores: the strip replayed at
            // load then starts exactly where this one does.
            Vector3d origin = body.GetRelSurfaceNVector(lat, lon);

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
            Guarded(PrepareAndConfirm);
        }

        // Runs one half of the click flow, turning anything unexpected into a single screen message.
        // Needed on BOTH halves rather than around the click: the second one runs from a dialog button,
        // long after ClearScatters has returned, so a try/catch there would no longer be on the stack.
        private void Guarded(Action step)
        {
            try
            {
                step();
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
            // Exclusive: starting a preview here stops whichever blade was running one. Not tidiness — a
            // preview costs a quad-registry walk and a per-instance mask test every frame, so two at once
            // would cost twice. The other blades' PAWs re-derive their own label from this same field, so
            // the UI follows without anyone having to tell it.
            ScatterHighlighter.Source = ScatterHighlighter.Source == this ? null : this;
            SyncHighlightLabel();
        }

        /// <summary>
        /// Dumps to KSP.log what the pass would decide, rock by rock, without changing anything.
        ///
        /// Test tool: it is what separates "the mask is in the wrong place" from "the blade is 5 m wide and
        /// those rocks are clusters anchored elsewhere". To be removed with the rest of Src/diagnostics.
        ///
        /// Hidden unless the debug window asks for it: it is an unlovely entry to leave in the PAW of every
        /// blade of a normal game.
        /// </summary>
        [KSPEvent(guiActive = false, guiActiveEditor = false, active = true,
                  guiName = "#LOC_BulldozerMod_dumpPass")]
        public void DumpPass()
        {
            try
            {
                BandDiagnostic.Dump(this, passLength, passWidth);
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

            // Derived, never assumed off: this blade may be the one already being previewed — a docking
            // brings in parts without a scene change.
            SyncHighlightLabel();

            SyncDumpButton();
        }

        public override void OnUpdate()
        {
            base.OnUpdate();
            if (!ParallaxInterop.Available)
            {
                return;
            }

            // The preview is exclusive and scene-wide while its label lives on a part, so ANOTHER blade
            // taking it over — or the overlay putting it out by itself — must not leave this PAW announcing
            // the opposite of the truth. Checked outside the window test below: the label has to be right
            // the frame the window opens, not one frame later.
            if (_highlightLabelOn != (ScatterHighlighter.Source == this))
            {
                SyncHighlightLabel();
            }

            // Same reason, and same place: the debug window can be toggled while this PAW is open, and the
            // window itself is what the player is looking at when they do it.
            if (Events["DumpPass"].guiActive != BandDiagnostic.ButtonVisible)
            {
                SyncDumpButton();
            }

            // Everything from here is for a PAW somebody is reading: the cost alone walks Parallax's whole
            // quad registry on every refresh.
            if (part.PartActionWindow == null || cellsLeft <= 0f)
            {
                // Due again the moment the window comes back rather than up to half a second later: what it
                // last showed was true before it was closed, and the pass button now hangs off that same
                // answer.
                _costTimer = 0f;
                return;
            }

            // Every frame, unlike the cost: it is one raycast, and the player reads it while working a
            // robotic hinge — a value refreshed twice a second is not something you can aim with. Only the
            // raycast is paid at that rate; the labels are rebuilt solely when what they say changes.
            SyncStance();

            _costTimer -= Time.deltaTime;
            if (_costTimer > 0f)
            {
                return;
            }
            _costTimer = CostRefreshInterval;
            SyncCost();
        }

        /// <summary>
        /// The world axis this blade sends its pass along, or <see cref="Vector3.zero"/> when there is none
        /// to be had.
        ///
        /// The blade's own facing transform, which is the one thing a dedicated part settles for free: it
        /// knows which way it looks, so "ahead" no longer has to be guessed from whatever the player last
        /// picked as a control point — and two blades on one rover aim apart instead of together.
        ///
        /// Falls back to the navball's nose — the control point's own +up axis (NavBall targets
        /// ActiveVessel.ReferenceTransform, and ModuleCommand draws its "forward" arrow along
        /// controlPoint.transform.up) — for a model that declares no such transform. So "control from here"
        /// then aims the pass exactly as it aims the rover, off an instrument already on screen.
        /// </summary>
        public Vector3 FacingAxis
        {
            get
            {
                Transform face = BladeFace;
                if (face != null)
                {
                    return face.forward;
                }
                Transform reference = vessel == null ? null : vessel.ReferenceTransform;
                return reference == null ? Vector3.zero : reference.up;
            }
        }

        /// <summary>
        /// The world point a pass by this blade starts from: its own cutting edge, or the part's origin when
        /// the meshes cannot be read at all.
        /// </summary>
        public Vector3 OriginPoint
        {
            get
            {
                return TryResolveLocalEdge()
                    ? part.transform.TransformPoint(_localEdge)
                    : part.transform.position;
            }
        }

        /// <summary>
        /// Where a pass by this blade starts from, as latitude/longitude: the blade's own cutting edge.
        ///
        /// The blade and not the vessel centre, which is what makes the thing a tool that is CARRIED rather
        /// than a property of the rover: a blade slung out on an arm clears the ground it is actually over,
        /// and two blades on one rover clear two different strips instead of the same one twice.
        ///
        /// Same point <see cref="BladeStance"/> measures, deliberately: what is checked to be down at ground
        /// level is exactly what the pass starts from.
        /// </summary>
        private void GetOrigin(out double lat, out double lon)
        {
            CelestialBody body = vessel.mainBody;

            // Unity scene space on both sides — CelestialBody.position is the body's own transform — so
            // the floating origin cancels out and no world-space conversion is needed.
            Vector3 point = OriginPoint;
            lat = body.GetLatitude(point);
            lon = body.GetLongitude(point);
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

        // Bound to the module whose slider moved: the price follows the sliders without waiting for the
        // refresh tick. The preview needs no telling — it reads this blade's fields straight off.
        private void OnPassChanged(BaseField field, object oldValue)
        {
            SyncCost();
        }

        // A toggle's label must announce what the NEXT click does, not the current state.
        private void SyncHighlightLabel()
        {
            _highlightLabelOn = ScatterHighlighter.Source == this;
            Events["ToggleHighlight"].guiName =
                ModLocalization.GetString(_highlightLabelOn ? "highlightOff" : "highlightOn");
        }

        // Nothing else to do to refresh an open window: KSP re-walks its items every update and drops the
        // ones whose event has gone guiActive = false, adding them back when it comes back.
        private void SyncDumpButton()
        {
            Events["DumpPass"].guiActive = BandDiagnostic.ButtonVisible;
        }

        // Wear left, and whether the blade can still be used at all.
        private void SyncWear()
        {
            wearStatus = ModLocalization.GetString("bladeWearValue",
                                                   Mathf.RoundToInt(cellsLeft),
                                                   Mathf.RoundToInt(cellCapacity));

            bool worn = cellsLeft <= 0f;
            Fields["costStatus"].guiActive = !worn;
            Fields["clearanceStatus"].guiActive = !worn;
            Fields["tiltStatus"].guiActive = !worn;

            SyncClearButton();
        }

        // Whether the pass button is offered at all. Two ways to lose it, and each of them says why on the
        // line right above it in the same window: a worn-out blade (the wear line) and ground another mod
        // has reserved (the cost line).
        //
        // Taken away rather than left to be clicked and turned down — but the click keeps its own refusal
        // all the same, because this is only ever as fresh as the last cost refresh, and a rover that has
        // rolled onto a reserved quad since then would otherwise pay a reload for nothing.
        private void SyncClearButton()
        {
            bool worn = cellsLeft <= 0f;
            Events["ClearScatters"].guiName = ModLocalization.GetString(worn ? "bladeWorn" : "clearScatters");
            Events["ClearScatters"].guiActive = !worn && !_reserved;
        }

        // What the pass currently set up would cost, or what stands in its way. Silent about anything it
        // cannot answer: an unusable heading already has its own refusal message at click time.
        private void SyncCost()
        {
            bool reserved;
            costStatus = CostLabel(out reserved);
            if (reserved != _reserved)
            {
                _reserved = reserved;
                SyncClearButton();
            }
        }

        // The cost line as the PAW shows it: the price of the pass THIS blade would make, or the decal that
        // makes it impossible — in which case <reserved> comes back true, which is what takes the pass
        // button away. Priced on the blade's own vessel, never on the active one: a PAW opened on a rover
        // parked next door must quote what that rover would do.
        //
        // The reservation comes out of the very quad list the price is computed from, so it costs nothing to
        // report and cannot disagree with the click, which works off the same list.
        private string CostLabel(out bool reserved)
        {
            reserved = false;

            if (vessel == null || vessel.mainBody == null || vessel.mainBody.pqsController == null)
            {
                return ModLocalization.GetString("passCostUnknown");
            }

            SurfaceBand band;
            if (!TryBuildBand(this, passLength, passWidth, out band))
            {
                return ModLocalization.GetString("passCostUnknown");
            }

            List<QuadRef> quads = QuadLocator.FindImpactedQuads(vessel.mainBody, band);

            // One taken quad turns the whole pass down at the click, so a price here would quote something
            // that is about to be refused. Named rather than merely flagged: the name IS the answer to
            // "why can't I clear here", and near the space center it reads ParallaxMaskKSC.
            //
            // ONE name, even where several mods block different quads of the same pass, and not always the
            // same one — see QuadLocator.FindForeignOwner for why that is accepted.
            PQSMod_MapDecalVertexRemoveScatter foreign = QuadLocator.FindForeignOwner(quads);
            if (foreign != null)
            {
                reserved = true;
                return ModLocalization.GetString("passCostReserved", foreign.gameObject.name);
            }

            return ModLocalization.GetString("passCostValue",
                                             PassCost.Compute(vessel.mainBody, band, quads));
        }

        // How the blade lies over the ground, as the PAW shows it. Both values name their limit as soon as
        // they break it: the player is holding a hinge control and needs to know which way to move it, not
        // just that the button has gone quiet.
        private void SyncStance()
        {
            BladeStance stance;
            if (!TryMeasureStance(out stance))
            {
                if (_shownClearanceCm != NoStance || _shownTiltDeg != NoStance)
                {
                    _shownClearanceCm = NoStance;
                    _shownTiltDeg = NoStance;
                    clearanceStatus = ModLocalization.GetString("bladeGroundUnknown");
                    tiltStatus = ModLocalization.GetString("bladeGroundUnknown");
                }
                return;
            }

            // The limits are tested on the raw value, exactly as the click's own refusal tests them, and the
            // verdict is cached alongside the rounded number: within one centimetre of the limit the same
            // "0.50" prints under two different sentences, and the PAW must not keep the reassuring one
            // while the click turns the pass down.
            bool clearanceOver = stance.clearance > MaxClearance;
            int clearanceCm = (int)Math.Round(stance.clearance * 100.0);
            if (clearanceCm != _shownClearanceCm || clearanceOver != _shownClearanceOver)
            {
                _shownClearanceCm = clearanceCm;
                _shownClearanceOver = clearanceOver;
                clearanceStatus = clearanceOver
                    ? ModLocalization.GetString("bladeClearanceTooHigh",
                                                stance.clearance.ToString("F2"), MaxClearance.ToString("F2"))
                    : ModLocalization.GetString("bladeClearanceValue", stance.clearance.ToString("F2"));
            }

            bool tiltOver = stance.tilt > MaxTilt;
            int tiltDeg = (int)Math.Round(stance.tilt);
            if (tiltDeg != _shownTiltDeg || tiltOver != _shownTiltOver)
            {
                _shownTiltDeg = tiltDeg;
                _shownTiltOver = tiltOver;
                tiltStatus = tiltOver
                    ? ModLocalization.GetString("bladeTiltTooSteep",
                                               stance.tilt.ToString("F0"), MaxTilt.ToString("F0"))
                    : ModLocalization.GetString("bladeTiltValue", stance.tilt.ToString("F0"));
            }
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

        /// <summary>
        /// Everything a pass has to satisfy, ending on the confirmation dialog. Charges nothing and writes
        /// nothing: <see cref="Commit"/> is what a confirmed pass actually does.
        /// </summary>
        private void PrepareAndConfirm()
        {
            // This blade's own vessel, not the active one: a PAW can be opened on a rover parked next door,
            // and the pass belongs to the blade that was clicked. Only the camera stays where it is — see
            // the focus index at the end.
            if (vessel == null || vessel.mainBody == null || vessel.mainBody.pqsController == null)
            {
                LOGGER.LogError("No vessel / body / PQS under this blade");
                return;
            }
            if (BulldozerScenario.Instance == null)
            {
                LOGGER.LogError("Scenario not available; cannot record action");
                return;
            }

            CelestialBody body = vessel.mainBody;

            SurfaceBand band;
            if (!TryBuildBand(this, passLength, passWidth, out band))
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
            // can never take ours (Parallax allows one decal per quad). The PAW has been saying so from the
            // same list, so this is the confirmation of a refusal the player has already read, not a
            // surprise.
            PQSMod_MapDecalVertexRemoveScatter foreign = QuadLocator.FindForeignOwner(quads);
            if (foreign != null)
            {
                LOGGER.LogWarning($"A quad under the pass is already owned by " +
                                  $"'{foreign.gameObject.name}' — refusing");
                Warn(ModLocalization.GetString("areaReserved", foreign.gameObject.name));
                return;
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

            // The pass is legal: ask before spending anything. The wear is the one thing a pass spends
            // that cannot be got back, and the reload is a manoeuvre the player has to be ready for — so
            // both are said here, with the numbers that decide them, rather than found out afterwards.
            //
            // Everything the dialog announces is settled HERE, and it is that snapshot Commit applies: the
            // price shown is the price charged and the strip cleared is the one that was priced, whatever
            // the rover drifts to while the dialog is up. The game keeps running behind a stock PopupDialog.
            //
            // Stock dialog rather than the shared uGUI popin: a popin needs a window of ours to live in,
            // and a PAW is not one.
            //
            // The very origin the band was built from, so the strip replayed at load starts where this
            // one did — at the blade's edge, not at the middle of the rover.
            double lat, lon;
            GetOrigin(out lat, out lon);

            int left = Mathf.RoundToInt(cellsLeft);
            PopupDialog.SpawnPopupDialog(
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new MultiOptionDialog(
                    "bulldozerPassConfirm",
                    ModLocalization.GetString("confirmMessage", cost, left, left - cost),
                    ModLocalization.GetString("confirmTitle"),
                    HighLogic.UISkin,
                    new DialogGUIButton(ModLocalization.GetString("confirmOk"),
                                        () => Guarded(() => Commit(body, band, quads, cost, lat, lon)),
                                        true),
                    new DialogGUIButton(ModLocalization.GetString("commonCancel"), () => { }, true)),
                false,
                HighLogic.UISkin);
        }

        /// <summary>
        /// Charges the blade, records the pass and reloads the game. Runs on the player's confirmation,
        /// against the snapshot <see cref="PrepareAndConfirm"/> priced — never a freshly measured pass.
        /// </summary>
        private void Commit(CelestialBody body, SurfaceBand band, List<QuadRef> quads, int cost,
                            double lat, double lon)
        {
            // Charged BEFORE the game is snapshotted below, or the reload would bring back a blade that
            // never paid for this pass.
            cellsLeft -= cost;
            SyncWear();

            BulldozerAction action = new BulldozerAction
            {
                bodyName = body.name,
                lat = lat,
                lon = lon,
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
            // The ACTIVE vessel, which is not necessarily this blade's: clearing from a neighbour's PAW
            // must not hand the player a different rover to fly when the scene comes back.
            int focusIdx = FlightGlobals.Vessels.IndexOf(FlightGlobals.ActiveVessel);
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
