using System.Collections.Generic;
using com.github.lhervier.ksp.shared;
using com.github.lhervier.ksp.bulldozermod.terrain;

namespace com.github.lhervier.ksp.bulldozermod.persistence
{
    /// <summary>
    /// Per-save store of cleaning actions (the single source of truth — never the in-memory body, which
    /// persists across saves via DontDestroyOnLoad). Added to ALL game modes so its OnLoad runs on every
    /// flight load, including saves that never carried the mod — that is what lets us purge decals leaked
    /// from a previously loaded save.
    ///
    /// OnLoad is also where we INJECT the Parallax exclusion decals. This runs during the loading screen,
    /// BEFORE FlightDriver -> PSystemSetup.SetPQSActive -> pqs.ForceStart() builds the active body's terrain
    /// (vessel still packed), which is what the decals need: they register themselves per quad in
    /// OnQuadPreBuild, so they must be in the mod list before the sphere builds. See ScatterCleaner.
    /// </summary>
    // FLIGHT ONLY — do NOT add a scene back without reading this first. OnLoad ends up calling
    // PQS.ResetAndWait() on every touched body, and rebuilding Kerbin's sphere while the space center
    // loads leaves KSC buildings that can no longer be clicked (measured on the Tracking Station and the
    // R&D: a quad rebuild runs PQSCity.OnSphereReset, which deactivates the facility objects the click
    // raycast needs). Reading the actions from another scene would need the injection moved out of
    // OnLoad first, not this list extended.
    //
    // Nothing outside flight needs the decals anyway — they only decide which scatters spawn on the
    // terrain the player is standing on — and the data survives a save made from another scene:
    // ScenarioRunner only refreshes the protoModules whose moduleRef is live (ScenarioRunner.cs:416),
    // the others keep the node they were loaded with.
    [KSPScenario(ScenarioCreationOptions.AddToAllGames, GameScenes.FLIGHT)]
    public class BulldozerScenario : ScenarioModule
    {
        private static readonly ModLogger LOGGER = new ModLogger("Scenario");

        public static BulldozerScenario Instance { get; private set; }

        private readonly List<BulldozerAction> _actions = new List<BulldozerAction>();

        /// <summary>The actions stored in this save, in application order.</summary>
        public List<BulldozerAction> Actions => _actions;

        public override void OnAwake()
        {
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        public override void OnLoad(ConfigNode node)
        {
            _actions.Clear();
            foreach (ConfigNode n in node.GetNodes("ACTION"))
            {
                BulldozerAction a = BulldozerAction.Load(n);
                if (a != null)
                {
                    _actions.Add(a);
                }
            }
            LOGGER.LogInfo($"Loaded {_actions.Count} action(s)");

            // Purge leaked decals + inject this save's decals NOW, before KSP's ForceStart builds the terrain.
            ScatterCleaner.InjectActions(_actions);
        }

        public override void OnSave(ConfigNode node)
        {
            foreach (BulldozerAction a in _actions)
            {
                a.Save(node.AddNode("ACTION"));
            }
        }

        /// <summary>Records a new action. The caller is expected to save+reload so the replay applies it.</summary>
        public void AddAction(BulldozerAction action)
        {
            _actions.Add(action);
        }
    }
}
