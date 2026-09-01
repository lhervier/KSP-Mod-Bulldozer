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

        /// <summary>Whether the blades offer their log-only diagnostic entry in the PAW.</summary>
        public bool ShowDumpButton { get; set; }

        /// <summary>How verbose the mod's logging is.</summary>
        public LogLevel LogLevel { get; set; }

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

            // Off by default: it is a test tool, and it sits in the PAW of every blade.
            ShowDumpButton = false;

            // Same default as the sibling mods: what the player is meant to see in KSP.log, no more.
            LogLevel = LogLevel.Info;
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

            bool showDumpButton = ShowDumpButton;
            general.TryGetValue("showDumpButton", ref showDumpButton);
            ShowDumpButton = showDumpButton;

            string logLevel = LogLevel.ToString();
            general.TryGetValue("logLevel", ref logLevel);
            // An unknown level (hand-edited file, or a level dropped from the enum) keeps the default
            // rather than silencing the log.
            LogLevel parsed;
            if (Enum.TryParse(logLevel, out parsed) && Enum.IsDefined(typeof(LogLevel), parsed))
            {
                LogLevel = parsed;
            }
            else
            {
                LOGGER.LogWarning($"Unknown log level '{logLevel}', keeping {LogLevel}");
            }

            LOGGER.LogInfo($"Settings loaded from {_path} (showCleanedZones={ShowCleanedZones}, " +
                           $"showDumpButton={ShowDumpButton}, logLevel={LogLevel})");
        }

        /// <summary>Writes the settings file, creating PluginData/ on first save.</summary>
        public void Save()
        {
            try
            {
                ConfigNode root = new ConfigNode();
                ConfigNode general = root.AddNode(GeneralNode);
                general.AddValue("showCleanedZones", ShowCleanedZones);
                general.AddValue("showDumpButton", ShowDumpButton);
                general.AddValue("logLevel", LogLevel.ToString());

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
