using UnityEngine;
using com.github.lhervier.ksp.shared.ugui.checkbox;

namespace com.github.lhervier.ksp.bulldozermod.ui.ugui
{
    /// <summary>
    /// Behaviour of the window content: each checkbox drives its switch through the view model, and is
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
        public ContentController WithControls(CheckboxController cleanedZonesCheckbox,
                                              CheckboxController dumpButtonCheckbox)
        {
            this._cleanedZonesCheckbox = cleanedZonesCheckbox;
            this._dumpButtonCheckbox = dumpButtonCheckbox;
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
        }

        private void OnCleanedZonesToggled(bool value)
        {
            _viewModel.ShowCleanedZones = value;
        }

        private void OnDumpButtonToggled(bool value)
        {
            _viewModel.ShowDumpButton = value;
        }
    }
}
