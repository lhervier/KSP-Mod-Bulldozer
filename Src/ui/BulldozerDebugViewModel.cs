using UnityEngine;
using com.github.lhervier.ksp.shared;
using com.github.lhervier.ksp.mod.bulldozer.diagnostics;
using com.github.lhervier.ksp.mod.bulldozer.parallax;
using com.github.lhervier.ksp.mod.bulldozer.settings;

namespace com.github.lhervier.ksp.mod.bulldozer.ui
{
    /// <summary>
    /// State of the debug window: plain properties that fire a KSP EventVoid on change, which the uGUI
    /// controllers subscribe to. Same shape as the sibling mods' view models.
    ///
    /// It is also the single place where a setting, its persisted copy and the overlay's static switch are
    /// kept in step — the overlay flag has to be static (its addon is rebuilt at every scene change) while
    /// the setting outlives the session, so something has to own that pairing.
    /// </summary>
    public class BulldozerDebugViewModel : MonoBehaviour
    {
        private BulldozerSettings _settings;

        /// <summary>Fired whenever <see cref="ShowCleanedZones"/> changes.</summary>
        public readonly EventVoid OnShowCleanedZonesChanged =
            new EventVoid("BulldozerDebugViewModel.OnShowCleanedZonesChanged");

        /// <summary>Fired whenever <see cref="ShowDumpButton"/> changes.</summary>
        public readonly EventVoid OnShowDumpButtonChanged =
            new EventVoid("BulldozerDebugViewModel.OnShowDumpButtonChanged");

        /// <summary>Fired whenever <see cref="LogLevel"/> changes.</summary>
        public readonly EventVoid OnLogLevelChanged =
            new EventVoid("BulldozerDebugViewModel.OnLogLevelChanged");

        /// <summary>Binds the view model to the settings and applies them. Call once, right after AddComponent.</summary>
        public BulldozerDebugViewModel WithSettings(BulldozerSettings settings)
        {
            _settings = settings;
            CleanedZonesOverlay.Active = settings.ShowCleanedZones;
            BandDiagnostic.ButtonVisible = settings.ShowDumpButton;
            ModLogger.SetLogLevel(settings.LogLevel);
            return this;
        }

        /// <summary>Whether the already-cleared cells are painted on the ground.</summary>
        public bool ShowCleanedZones
        {
            get { return _settings != null && _settings.ShowCleanedZones; }
            set
            {
                if (_settings == null || _settings.ShowCleanedZones == value)
                {
                    return;
                }
                _settings.ShowCleanedZones = value;
                _settings.Save();
                CleanedZonesOverlay.Active = value;
                OnShowCleanedZonesChanged.Fire();
            }
        }

        /// <summary>Whether the blades offer their log-only diagnostic entry in the PAW.</summary>
        public bool ShowDumpButton
        {
            get { return _settings != null && _settings.ShowDumpButton; }
            set
            {
                if (_settings == null || _settings.ShowDumpButton == value)
                {
                    return;
                }
                _settings.ShowDumpButton = value;
                _settings.Save();
                BandDiagnostic.ButtonVisible = value;
                OnShowDumpButtonChanged.Fire();
            }
        }

        /// <summary>How verbose the mod's logging is.</summary>
        public LogLevel LogLevel
        {
            get { return _settings != null ? _settings.LogLevel : LogLevel.Info; }
            set
            {
                if (_settings == null || _settings.LogLevel == value)
                {
                    return;
                }
                _settings.LogLevel = value;
                _settings.Save();
                ModLogger.SetLogLevel(value);
                OnLogLevelChanged.Fire();
            }
        }
    }
}
