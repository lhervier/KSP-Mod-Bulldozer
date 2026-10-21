using System;
using UnityEngine;
using com.github.lhervier.ksp.shared;
using com.github.lhervier.ksp.shared.ugui.checkbox;
using com.github.lhervier.ksp.shared.ugui.combo;

namespace com.github.lhervier.ksp.mod.bulldozer.ui.ugui
{
    /// <summary>
    /// Behaviour of the window content: each control drives its setting through the view model, and is
    /// synced back from it whenever the window is shown.
    ///
    /// The sync matters here rather than being belt-and-braces: the window is rebuilt from scratch every
    /// time KSP destroys it, while the settings outlive the session — so what a box shows has to come
    /// from the setting, never from whatever it was built with.
    /// </summary>
    public class ContentController : MonoBehaviour
    {
        private BulldozerDebugViewModel _viewModel;
        public ContentController WithViewModel(BulldozerDebugViewModel viewModel)
        {
            this._viewModel = viewModel;
            return this;
        }

        private CheckboxController _cleanedZonesCheckbox;
        private CheckboxController _dumpButtonCheckbox;
        private ComboController _logLevelCombo;
        public ContentController WithControls(CheckboxController cleanedZonesCheckbox,
                                              CheckboxController dumpButtonCheckbox,
                                              ComboController logLevelCombo)
        {
            this._cleanedZonesCheckbox = cleanedZonesCheckbox;
            this._dumpButtonCheckbox = dumpButtonCheckbox;
            this._logLevelCombo = logLevelCombo;
            return this;
        }

        public void Start()
        {
            if (_cleanedZonesCheckbox != null)
            {
                _cleanedZonesCheckbox.OnToggled.Add(OnCleanedZonesToggled);
            }
            if (_dumpButtonCheckbox != null)
            {
                _dumpButtonCheckbox.OnToggled.Add(OnDumpButtonToggled);
            }
            // The options only exist once: OnEnable then only selects among them.
            if (_logLevelCombo != null)
            {
                _logLevelCombo.SetOptions(LogLevels.Names, _viewModel.LogLevel.ToString());
                _logLevelCombo.OnSelect.Add(OnLogLevelSelected);
            }
            // The combo does not select on its own click, on purpose: what it displays comes back from
            // the setting, so a value the view model refuses is never shown as chosen.
            if (_viewModel != null)
            {
                _viewModel.OnLogLevelChanged.Add(OnLogLevelChanged);
            }
        }

        public void OnDestroy()
        {
            if (_cleanedZonesCheckbox != null)
            {
                _cleanedZonesCheckbox.OnToggled.Remove(OnCleanedZonesToggled);
            }
            if (_dumpButtonCheckbox != null)
            {
                _dumpButtonCheckbox.OnToggled.Remove(OnDumpButtonToggled);
            }
            if (_logLevelCombo != null)
            {
                _logLevelCombo.OnSelect.Remove(OnLogLevelSelected);
            }
            if (_viewModel != null)
            {
                _viewModel.OnLogLevelChanged.Remove(OnLogLevelChanged);
            }
        }

        // Guarded: OnEnable can fire during AddComponent, before the view model is injected.
        public void OnEnable()
        {
            if (_viewModel == null) return;
            if (_cleanedZonesCheckbox != null)
            {
                _cleanedZonesCheckbox.SetChecked(_viewModel.ShowCleanedZones);
            }
            if (_dumpButtonCheckbox != null)
            {
                _dumpButtonCheckbox.SetChecked(_viewModel.ShowDumpButton);
            }
            if (_logLevelCombo != null)
            {
                _logLevelCombo.Select(_viewModel.LogLevel.ToString());
            }
        }

        private void OnCleanedZonesToggled(bool value)
        {
            _viewModel.ShowCleanedZones = value;
        }

        private void OnDumpButtonToggled(bool value)
        {
            _viewModel.ShowDumpButton = value;
        }

        private void OnLogLevelSelected(string value)
        {
            LogLevel level;
            if (Enum.TryParse(value, out level))
            {
                _viewModel.LogLevel = level;
            }
        }

        // Instance method, not static: EventVoid.Add dereferences the delegate's target.
        private void OnLogLevelChanged()
        {
            if (_logLevelCombo != null)
            {
                _logLevelCombo.Select(_viewModel.LogLevel.ToString());
            }
        }
    }
}
