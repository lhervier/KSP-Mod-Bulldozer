using UnityEngine;
using com.github.lhervier.ksp.shared;
using com.github.lhervier.ksp.bulldozermod.parallax;
using com.github.lhervier.ksp.bulldozermod.persistence;

namespace com.github.lhervier.ksp.bulldozermod
{
    /// <summary>
    /// PAW entry that clears the scatters (Parallax rocks and the like) around the vessel.
    ///
    /// Patched onto every command module for now — the real thing will be a dedicated bulldozer blade part.
    ///
    /// Click flow: record the action in the per-save ScenarioModule, save to a dedicated file, reload. The
    /// decal is injected by the scenario's OnLoad while the PQS is still cold. Nothing is applied live: the
    /// exclusion is decided when Parallax builds a quad's scatters, so it needs a fresh build to take effect.
    /// </summary>
    public class BulldozerCleanModule : PartModule
    {
        private static readonly ModLogger LOGGER = new ModLogger("CleanModule");

        // Radius (m) actually cleared around the vessel. Hard-coded for the first test.
        private const double ClearRadius = 20.0;

        // Dedicated reload save (NOT "persistent"): the reload is a mod manoeuvre, not a save the player
        // asked for, and a scene-exit autosave to "persistent" could clobber it. Same as KSP-ReRoot.
        private const string ReloadSaveName = "BulldozerReload";

        [KSPEvent(guiActive = true, guiActiveEditor = false, active = true,
                  guiName = "#LOC_BulldozerMod_clearScatters")]
        public void ClearScatters()
        {
            try
            {
                RecordAndReload();
            }
            catch (System.Exception e)
            {
                LOGGER.LogError("Clear failed: " + e);
                ScreenMessages.PostScreenMessage(ModLocalization.GetString("clearFailed"),
                                                 5f, ScreenMessageStyle.UPPER_CENTER);
            }
        }

        public override void OnStart(StartState state)
        {
            base.OnStart(state);

            // Nothing to clear without Parallax: hide the entry rather than offer a no-op.
            if (!ParallaxInterop.Available)
            {
                Events["ClearScatters"].guiActive = false;
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
            BulldozerAction action = new BulldozerAction
            {
                bodyName = body.name,
                lat = vessel.latitude,
                lon = vessel.longitude,
                radius = ClearRadius
            };
            BulldozerScenario.Instance.AddAction(action);
            LOGGER.LogInfo($"Recorded action on {body.name} lat={action.lat:F5} lon={action.lon:F5} " +
                           $"radius={action.radius:F0}m");

            // The new action is serialized through the scenario's OnSave. Nothing else is patched: the vessel
            // keeps its saved pose, and since no terrain height changes it reappears exactly where it was.
            int focusIdx = FlightGlobals.Vessels.IndexOf(vessel);
            Game game = HighLogic.CurrentGame.Updated();
            GamePersistence.SaveGame(game, ReloadSaveName, HighLogic.SaveFolder, SaveMode.OVERWRITE);
            FlightDriver.StartAndFocusVessel(ReloadSaveName, focusIdx);
        }
    }
}
