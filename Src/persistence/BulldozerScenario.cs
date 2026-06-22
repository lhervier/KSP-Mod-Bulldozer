using System.Collections.Generic;
using com.github.lhervier.ksp.shared;

namespace com.github.lhervier.ksp.bulldozermod.persistence
{
    /// <summary>
    /// Per-save store of levelling actions (the single source of truth — never the in-memory body, which
    /// persists across saves via DontDestroyOnLoad). Added to ALL game modes so its OnLoad runs on every
    /// save load, including saves that never carried the mod — that is what lets the terrain applier purge
    /// deformations leaked from a previously loaded save.
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
