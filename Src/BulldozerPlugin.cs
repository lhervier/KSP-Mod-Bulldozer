using KSP.UI.Screens;
using UnityEngine;
using com.github.lhervier.ksp.shared;
using com.github.lhervier.ksp.bulldozermod.persistence;

namespace com.github.lhervier.ksp.bulldozermod
{
    /// <summary>
    /// Flight entry point: a toolbar button that levels the terrain around the active vessel.
    ///
    /// Click flow: record a levelling action in the per-save ScenarioModule, then save + reload focusing the
    /// vessel. The deformation is (re)applied by the scenario's OnLoad, BEFORE KSP ground-positions the
    /// vessel — so KSP seats and aligns the vessel on the already-flat terrain itself (no manual levelling).
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class BulldozerPlugin : MonoBehaviour
    {
        private static readonly ModLogger LOGGER = new ModLogger("Plugin");

        // Levelling radius around the vessel, in meters. Hard-coded for now. Kept large because the PQS
        // vertex grain is ~64-190 m depending on the body: a smaller disk would contain no vertices.
        private const double FlattenRadius = 500.0;

        // Dedicated reload save (NOT "persistent"): a scene-exit autosave to "persistent" could clobber it
        // before the reload reads it back. Same approach as the sibling KSP-ReRoot mod.
        private const string ReloadSaveName = "BulldozerReload";

        private ApplicationLauncherButton _toolbarButton;

        private void Start()
        {
            ModLogger.SetLogLevel(LogLevel.Debug);
            GameEvents.onGUIApplicationLauncherReady.Add(OnLauncherReady);
            LOGGER.LogInfo("Started");
        }

        private void OnDestroy()
        {
            GameEvents.onGUIApplicationLauncherReady.Remove(OnLauncherReady);
            RemoveToolbarButton();
        }

        // ==========================================================================
        // Toolbar
        // ==========================================================================

        private void OnLauncherReady()
        {
            if (_toolbarButton != null)
            {
                return;
            }
            try
            {
                _toolbarButton = ApplicationLauncher.Instance.AddModApplication(
                    OnClick,    // onTrue : record the action and reload
                    Noop,       // onFalse: nothing (button is momentary, see OnClick)
                    null, null, null, null,
                    ApplicationLauncher.AppScenes.FLIGHT,
                    GameDatabase.Instance.GetTexture(Constants.ModName + "/icon", false) ?? Texture2D.whiteTexture);
            }
            catch (System.Exception e)
            {
                LOGGER.LogError("Error creating toolbar button: " + e.Message);
            }
        }

        private void RemoveToolbarButton()
        {
            if (_toolbarButton == null)
            {
                return;
            }
            try
            {
                ApplicationLauncher.Instance.RemoveModApplication(_toolbarButton);
            }
            catch (System.Exception e)
            {
                LOGGER.LogError("Error removing toolbar button: " + e.Message);
            }
            _toolbarButton = null;
        }

        // ==========================================================================
        // Action
        // ==========================================================================

        private void OnClick()
        {
            try
            {
                RecordAndReload();
            }
            catch (System.Exception e)
            {
                LOGGER.LogError("Flatten click failed: " + e);
            }
            finally
            {
                // Momentary button: reset its toggle without firing onFalse (SetFalse(false)).
                if (_toolbarButton != null)
                {
                    _toolbarButton.SetFalse(false);
                }
            }
        }

        private void RecordAndReload()
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null || vessel.mainBody == null)
            {
                LOGGER.LogError("No active vessel / body");
                return;
            }
            if (BulldozerScenario.Instance == null)
            {
                LOGGER.LogError("Scenario not available; cannot record action");
                return;
            }

            CelestialBody body = vessel.mainBody;
            double lat = vessel.latitude;
            double lon = vessel.longitude;

            BulldozerAction action = new BulldozerAction
            {
                bodyName = body.name,
                lat = lat,
                lon = lon,
                radius = FlattenRadius,
                // Flatten to the terrain height under the rover's reference point. We do NOT reposition the
                // vessel: it loads at its saved pose, the flatten lowers the ground to this level, and the
                // vessel simply drops/settles onto the flat area on its own.
                flattenTo = body.TerrainAltitude(lat, lon, true)
            };
            BulldozerScenario.Instance.AddAction(action);
            LOGGER.LogInfo($"Recorded action on {body.name} lat={lat:F5} lon={lon:F5} flattenTo={action.flattenTo:F2}");

            PrepareVesselForReseat(vessel, body);

            // Save to the dedicated file (the new action is serialized via the scenario's OnSave), then
            // reload focusing the same vessel. The scenario's OnLoad applies the flatten before KSP seats it.
            int focusIdx = FlightGlobals.Vessels.IndexOf(vessel);
            Game game = HighLogic.CurrentGame.Updated();
            GamePersistence.SaveGame(game, ReloadSaveName, HighLogic.SaveFolder, SaveMode.OVERWRITE);
            FlightDriver.StartAndFocusVessel(ReloadSaveName, focusIdx);
        }

        // Makes KSP re-seat AND re-level the vessel on the freshly-flattened terrain at reload, instead of it
        // reappearing at its old (above-flat, tilted) pose and dropping/toppling. Two save fields drive this:
        //
        //   - PQSMin/PQSMax = 0: GoOffRails compares the saved PQS subdivision levels to the body's current ones
        //     to decide skipGroundPositioning. Zeroing them (the same sentinel KSP itself writes for spawned
        //     vessels) forces skipGroundPositioning = false, so CheckGroundCollision raycasts down from +5000 m
        //     and seats the vessel on the flat mesh at the right altitude, regardless of the saved altitude.
        //
        //   - srfRelRotation: only this attitude is persisted, and CheckGroundCollision's re-orientation term
        //     collapses to identity on flat ground (it recomputes terrainNormal from the same flat surface the
        //     raycast hits), so it KEEPS whatever attitude we load. We therefore pre-level it here: align the
        //     vessel's up with the local radial (the flat-ground normal), keeping heading. Writing the field
        //     directly (no SetRotation) avoids jolting live physics; SaveGame is synchronous so no per-frame
        //     update overwrites it before the snapshot.
        private static void PrepareVesselForReseat(Vessel vessel, CelestialBody body)
        {
            Vector3d radialUp = (vessel.GetWorldPos3D() - body.position).normalized;
            Quaternion levelWorld = Quaternion.FromToRotation(vessel.vesselTransform.up, (Vector3)radialUp)
                                    * vessel.vesselTransform.rotation;
            vessel.srfRelRotation = Quaternion.Inverse(body.bodyTransform.rotation) * levelWorld;

            vessel.PQSminLevel = 0;
            vessel.PQSmaxLevel = 0;
        }

        private void Noop()
        {
        }
    }
}
