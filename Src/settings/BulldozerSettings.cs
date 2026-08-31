using System;
using System.IO;
using System.Reflection;
using com.github.lhervier.ksp.shared;

namespace com.github.lhervier.ksp.bulldozermod.settings
{
    /// <summary>
    /// The mod's own settings, per INSTALL rather than per save — they describe how the player wants the
    /// mod to behave, not what has been done to a game. The cleaned areas themselves are save data and live
    /// in the scenario; nothing here ever describes terrain.
    ///
    /// Stored in PluginData/ next to the DLL: GameDatabase skips that folder, so KSP never tries to read
    /// this file as a part config.
    /// </summary>
    public class BulldozerSettings
    {
        private static readonly ModLogger LOGGER = new ModLogger("Settings");

        private const string ConfigFile = "bulldozer.cfg";
        private const string GeneralNode = "GENERAL";

        private readonly string _path;

        /// <summary>Whether the debug overlay paints the already-cleared cells on the ground.</summary>
        public bool ShowCleanedZones { get; set; }

        public BulldozerSettings()
        {
            string dll = Assembly.GetExecutingAssembly().Location;
            _path = Path.Combine(Path.GetDirectoryName(dll), Path.Combine("PluginData", ConfigFile));
            Reset();
        }

        private void Reset()
        {
            // Off by default: it is a debug view, and it costs a raycast per cell corner to build.
            ShowCleanedZones = false;
        }

        /// <summary>Reads the settings file, falling back on the defaults when it is missing or unreadable.</summary>
        public void Load()
        {
            Reset();
            if (!File.Exists(_path))
            {
                LOGGER.LogInfo("No settings file yet, using the defaults");
                return;
            }

            ConfigNode root = ConfigNode.Load(_path);
            ConfigNode general = root?.GetNode(GeneralNode);
            if (general == null)
            {
                LOGGER.LogWarning($"Unusable settings file '{_path}', using the defaults");
                return;
            }

            bool showCleanedZones = ShowCleanedZones;
            general.TryGetValue("showCleanedZones", ref showCleanedZones);
            ShowCleanedZones = showCleanedZones;

            LOGGER.LogInfo($"Settings loaded from {_path} (showCleanedZones={ShowCleanedZones})");
        }

        /// <summary>Writes the settings file, creating PluginData/ on first save.</summary>
        public void Save()
        {
            try
            {
                ConfigNode root = new ConfigNode();
                ConfigNode general = root.AddNode(GeneralNode);
                general.AddValue("showCleanedZones", ShowCleanedZones);

                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                root.Save(_path);
            }
            catch (Exception e)
            {
                // A setting that fails to persist is not worth interrupting a flight for.
                LOGGER.LogError($"Could not save the settings to '{_path}': {e.Message}");
            }
        }
    }
}
