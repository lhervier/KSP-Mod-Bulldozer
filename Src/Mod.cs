using UnityEngine;
using com.github.lhervier.ksp.shared;
using com.github.lhervier.ksp.mod.bulldozer.settings;

namespace com.github.lhervier.ksp.mod.bulldozer
{
    /// <summary>
    /// One-shot startup addon: applies the persisted log level to the shared logger, once for the whole
    /// KSP session.
    ///
    /// It exists because the level has to be in place before anything logs, and the debug window — which
    /// is where the player changes it — only reaches flight. The scenario's OnLoad, where the decals are
    /// injected, runs while the flight scene is still loading: a level applied by the window would come
    /// too late to see exactly the messages a Debug/Trace level is turned on for.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.PSystemSpawn, true)]
    public class Mod : MonoBehaviour
    {
        private static readonly ModLogger LOGGER = new ModLogger("Mod");

        private void Start()
        {
            BulldozerSettings settings = new BulldozerSettings();
            settings.Load();
            ModLogger.SetLogLevel(settings.LogLevel);
            LOGGER.LogInfo($"Log level set to {settings.LogLevel}");
        }
    }
}
