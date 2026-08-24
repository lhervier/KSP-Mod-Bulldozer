using System.Collections.Generic;
using UnityEngine;
using com.github.lhervier.ksp.shared;
using com.github.lhervier.ksp.bulldozermod.parallax;
using com.github.lhervier.ksp.bulldozermod.persistence;
using com.github.lhervier.ksp.bulldozermod.terrain;

namespace com.github.lhervier.ksp.bulldozermod
{
    /// <summary>
    /// PAW entry that clears the scatters (Parallax rocks and the like) around the vessel.
    ///
    /// Patched onto every command module for now — the real thing will be a dedicated bulldozer blade part.
    ///
    /// Click flow: check the area is clearable, record the action (with the quads it covers) in the per-save
    /// ScenarioModule, save to a dedicated file, reload. The decals are injected by the scenario's OnLoad
    /// while the PQS is still cold. Nothing is applied live: the exclusion is decided when Parallax builds a
    /// quad's scatters, so it needs a fresh build to take effect.
    /// </summary>
    public class BulldozerCleanModule : PartModule
    {
        private static readonly ModLogger LOGGER = new ModLogger("CleanModule");

        // Radius (m) actually cleared around the vessel. Hard-coded for now.
        private const double ClearRadius = 20.0;

        // Below this, the disk is only a handful of pixels in the mask (~0.7 m/pixel), so we refuse instead
        // of producing a ragged blob.
        private const double MinimumRadius = 5.0;

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
                Warn(ModLocalization.GetString("clearFailed"));
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
            if (!RadiusIsUsable(ClearRadius))
            {
                return;
            }

            CelestialBody body = vessel.mainBody;

            // The quads are captured HERE, in flight: at load time the terrain is not built yet and their
            // geometry cannot be recovered.
            double quadHalfDiagonal;
            List<QuadLocator.QuadRef> quads = QuadLocator.FindImpactedQuads(
                body, vessel.latitude, vessel.longitude, ClearRadius, out quadHalfDiagonal);
            if (quads.Count == 0)
            {
                LOGGER.LogError("No leaf quad found under the vessel — is the terrain fully built?");
                Warn(ModLocalization.GetString("clearFailed"));
                return;
            }

            // Refuse now rather than after a pointless reload: a quad already claimed by another mod's decal
            // can never take ours (Parallax allows one decal per quad).
            foreach (QuadLocator.QuadRef quad in quads)
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
                radius = ClearRadius,
                maxLevel = body.pqsController.maxLevel,
                quadHalfDiagonal = quadHalfDiagonal
            };
            foreach (QuadLocator.QuadRef quad in quads)
            {
                action.quads.Add(quad.direction);
            }
            BulldozerScenario.Instance.AddAction(action);
            LOGGER.LogInfo($"Recorded action on {body.name} lat={action.lat:F5} lon={action.lon:F5} " +
                           $"radius={action.radius:F0}m over {action.quads.Count} quad(s), " +
                           $"quad half-diagonal {quadHalfDiagonal:F0} m");

            // The new action is serialized through the scenario's OnSave. Nothing else is patched: the vessel
            // keeps its saved pose, and since no terrain height changes it reappears exactly where it was.
            int focusIdx = FlightGlobals.Vessels.IndexOf(vessel);
            Game game = HighLogic.CurrentGame.Updated();
            GamePersistence.SaveGame(game, ReloadSaveName, HighLogic.SaveFolder, SaveMode.OVERWRITE);
            FlightDriver.StartAndFocusVessel(ReloadSaveName, focusIdx);
        }

        // Kept as a method taking the radius: it will earn its keep once the radius becomes a player setting.
        private static bool RadiusIsUsable(double radius)
        {
            if (radius >= MinimumRadius)
            {
                return true;
            }
            Warn(ModLocalization.GetString("radiusTooSmall", MinimumRadius.ToString("F0")));
            return false;
        }

        private static void Warn(string message)
        {
            ScreenMessages.PostScreenMessage(message, 5f, ScreenMessageStyle.UPPER_CENTER);
        }
    }
}
