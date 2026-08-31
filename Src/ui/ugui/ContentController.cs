using UnityEngine;
using com.github.lhervier.ksp.shared.ugui.checkbox;

namespace com.github.lhervier.ksp.bulldozermod.ui.ugui
{
    /// <summary>
    /// Behaviour of the window content: the checkbox drives the cleaned-zone overlay through the view
    /// model, and is synced back from it whenever the window is shown.
    ///
    /// The sync matters here rather than being belt-and-braces: the window is rebuilt from scratch every
    /// time KSP destroys it, while the setting outlives the session — so what the box shows has to come
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
        public ContentController WithControls(CheckboxController cleanedZonesCheckbox)
        {
            this._cleanedZonesCheckbox = cleanedZonesCheckbox;
            return this;
        }

        public void Start()
        {
            if (_cleanedZonesCheckbox != null)
            {
                _cleanedZonesCheckbox.OnToggled.Add(OnCleanedZonesToggled);
            }
        }

        public void OnDestroy()
        {
            if (_cleanedZonesCheckbox != null)
            {
                _cleanedZonesCheckbox.OnToggled.Remove(OnCleanedZonesToggled);
            }
        }

        // Guarded: OnEnable can fire during AddComponent, before the view model is injected.
        public void OnEnable()
        {
            if (_viewModel == null || _cleanedZonesCheckbox == null) return;
            _cleanedZonesCheckbox.SetChecked(_viewModel.ShowCleanedZones);
        }

        private void OnCleanedZonesToggled(bool value)
        {
            _viewModel.ShowCleanedZones = value;
        }
    }
}
