using System.Collections.Generic;
using com.github.lhervier.ksp.shared;
using com.github.lhervier.ksp.bulldozermod.terrain;

namespace com.github.lhervier.ksp.bulldozermod.persistence
{
    /// <summary>
    /// Per-save store of levelling actions (the single source of truth — never the in-memory body, which
    /// persists across saves via DontDestroyOnLoad). Added to ALL game modes so its OnLoad runs on every
    /// save load, including saves that never carried the mod — that is what lets us purge deformations
    /// leaked from a previously loaded save.
    ///
    /// OnLoad is also where we INJECT our flatten mods into the PQS. This runs during the loading screen,
    /// BEFORE FlightDriver -> PSystemSetup.SetPQSActive -> pqs.ForceStart() builds the active body's terrain
    /// (vessel still packed). So KSP's own cold build produces flat quads from our mods — no live rebuild,
    /// no "vessel holds its tile" bump. See TerrainLeveler.
    /// </summary>
    [KSPScenario(ScenarioCreationOptions.AddToAllGames, GameScenes.FLIGHT, GameScenes.SPACECENTER, GameScenes.TRACKSTATION)]
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

            // Purge leaked deformations + inject this save's flatten mods NOW, before KSP's ForceStart builds
            // the terrain. We do NOT rebuild — KSP does, cold, with the vessel still packed.
            TerrainLeveler.InjectActions(_actions);
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
