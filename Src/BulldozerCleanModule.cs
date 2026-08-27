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
    /// PAW entry that clears the scatters (Parallax rocks and the like) along a blade pass: a strip running
    /// straight ahead of the vessel, as long and as wide as the player picks.
    ///
    /// Patched onto every command module for now — the real thing will be a dedicated bulldozer blade part,
    /// which is also what will make "straight ahead" unambiguous without asking the control point.
    ///
    /// Click flow: work out the strip, check it is clearable, record the pass (with the quads it covers) in
    /// the per-save ScenarioModule, save to a dedicated file, reload. The decals are injected by the
    /// scenario's OnLoad while the PQS is still cold. Nothing is applied live: the exclusion is decided when
    /// Parallax builds a quad's scatters, so it needs a fresh build to take effect.
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
        private const float MinWidth = Step;
        private const float MaxWidth = 20f;

        // Two cells rather than the minimum: at exactly one cell the strip sits ON the mask's resolution
        // limit, where the cleared corridor can shift half a cell off the blade and leave up to 12% of its
        // own track standing. A default should not start life on a known limit.
        private const float DefaultWidth = 2f * Step;

        // Dedicated reload save (NOT "persistent"): the reload is a mod manoeuvre, not a save the player
        // asked for, and a scene-exit autosave to "persistent" could clobber it. Same as KSP-ReRoot.
        private const string ReloadSaveName = "BulldozerReload";

        /// <summary>
        /// Length (m) of the pass the overlays should announce.
        ///
        /// Static because the highlight and the quad overlay are scene-wide addons with no PAW of their own,
        /// while this value belongs to a part. It follows whichever PAW the player last touched; with several
        /// command pods, each keeping its own persisted value, a fresh scene leaves whichever started last —
        /// and the first slider move settles it.
        /// </summary>
        public static double SelectedLength = 50.0;

        /// <summary>Width (m) of the pass the overlays should announce. Same caveat as
        /// <see cref="SelectedLength"/>.</summary>
        public static double SelectedWidth = DefaultWidth;

        /// <summary>Length (m) of the pass this part would clear. Player setting, persisted per part.</summary>
        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = false,
                  guiName = "#LOC_BulldozerMod_passLength", guiUnits = "m", guiFormat = "F0")]
        [UI_FloatRange(minValue = MinLength, maxValue = MaxLength, stepIncrement = Step,
                       scene = UI_Scene.Flight)]
        public float passLength = 50f;

        /// <summary>Width (m) of the pass this part would clear. Player setting, persisted per part.</summary>
        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = false,
                  guiName = "#LOC_BulldozerMod_passWidth", guiUnits = "m", guiFormat = "F0")]
        [UI_FloatRange(minValue = MinWidth, maxValue = MaxWidth, stepIncrement = Step,
                       scene = UI_Scene.Flight)]
        public float passWidth = DefaultWidth;

        /// <summary>
        /// The strip a pass of <paramref name="length"/> by <paramref name="width"/> meters would clear right
        /// now: from the vessel, straight ahead. Returns false when the vessel points nowhere usable.
        ///
        /// Shared with the overlays on purpose — they must aim at the very strip a click would apply.
        /// </summary>
        public static bool TryBuildBand(Vessel vessel, double length, double width, out SurfaceBand band)
        {
            band = new SurfaceBand();
            if (vessel == null || vessel.mainBody == null || vessel.ReferenceTransform == null)
            {
                return false;
            }

            CelestialBody body = vessel.mainBody;

            // The origin goes through lat/lon because that is what the action stores: the strip replayed at
            // load then starts exactly where this one does.
            Vector3d origin = body.GetRelSurfaceNVector(vessel.latitude, vessel.longitude);

            // The navball's nose, which is the control point's own +up axis (NavBall targets
            // ActiveVessel.ReferenceTransform, and ModuleCommand draws its "forward" arrow along
            // controlPoint.transform.up). So "control from here" aims the pass exactly as it aims the rover,
            // and the player reads the heading off an instrument that is already on screen.
            Vector3d nose = body.GetRelSurfaceDirection(vessel.ReferenceTransform.up);
            double vertical = Vector3d.Dot(nose, origin);
            Vector3d heading = nose - origin * vertical;

            // The control axis has to be MORE HORIZONTAL THAN VERTICAL, i.e. within 45 deg of the horizon.
            // A small threshold does not work, and that is not theoretical: on a slope, a control point
            // aimed at the SKY still leaves a sizeable horizontal residue — the direction the deck happens
            // to lean, which is not a heading at all. Measured in game on the Mun with a Mk2 lander can:
            // its "Up" and "Reversed" control points (orientation 0,0,180, so nose down) both passed a
            // sin(10 deg) test and sent a 200 m pass off in an arbitrary direction — the two being exactly
            // 180 deg apart, which is the giveaway that both were reading the same tilt.
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

        public override void OnStart(StartState state)
        {
            base.OnStart(state);

            // Nothing to clear without Parallax: hide the entries rather than offer a no-op.
            if (!ParallaxInterop.Available)
            {
                Events["ClearScatters"].guiActive = false;
                Events["ToggleHighlight"].guiActive = false;
                Events["DumpPass"].guiActive = false;
                Fields["passLength"].guiActive = false;
                Fields["passWidth"].guiActive = false;
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

            // The flag outlives the scene, so the label has to be re-derived rather than assumed off.
            SyncHighlightLabel();
        }

        // Bound to the module whose slider moved, which is the point: it publishes THAT part's values, so the
        // overlays follow the PAW the player is actually using.
        private void OnPassChanged(BaseField field, object oldValue)
        {
            PublishPass();
        }

        private void PublishPass()
        {
            SelectedLength = passLength;
            SelectedWidth = passWidth;
        }

        // A toggle's label must announce what the NEXT click does, not the current state.
        private void SyncHighlightLabel()
        {
            Events["ToggleHighlight"].guiName =
                ModLocalization.GetString(ScatterHighlighter.Active ? "highlightOff" : "highlightOn");
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
                LOGGER.LogWarning("The control point gives no usable heading — refusing the pass");
                Warn(ModLocalization.GetString("headingUnusable"));
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
                           $"over {action.quads.Count} quad(s)");

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
