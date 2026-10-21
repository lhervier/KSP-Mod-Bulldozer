using UnityEngine;
using UnityEngine.UI;
using com.github.lhervier.ksp.shared;
using com.github.lhervier.ksp.shared.ugui;
using com.github.lhervier.ksp.shared.ugui.checkbox;
using com.github.lhervier.ksp.shared.ugui.combo;
using com.github.lhervier.ksp.mod.bulldozer.ui.styles;

namespace com.github.lhervier.ksp.mod.bulldozer.ui.ugui
{
    /// <summary>
    /// Popup content (everything below the shared title bar): the debug switches, one per row, then the
    /// log level combo, then the quads readout. Mounted and stretched to fill the content host by the
    /// PopupBuilder, so it only fills what it is given.
    /// </summary>
    public class ContentBuilder : IUGUIBuilder<ContentController>
    {
        private BulldozerDebugViewModel _viewModel;
        public ContentBuilder WithViewModel(BulldozerDebugViewModel viewModel)
        {
            this._viewModel = viewModel;
            return this;
        }

        public ContentController Build()
        {
            var rootGo = new GameObject("Bulldozer.Content", typeof(RectTransform));

            var layout = rootGo.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(
                Mathf.RoundToInt(BulldozerPalette.ContentPaddingH),
                Mathf.RoundToInt(BulldozerPalette.ContentPaddingH),
                Mathf.RoundToInt(BulldozerPalette.ContentPaddingV),
                Mathf.RoundToInt(BulldozerPalette.ContentPaddingV));
            layout.spacing = BulldozerPalette.ContentSpacing;
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            // Whole row clickable: the label is the target the player aims for, not the 12 px box.
            CheckboxController cleanedZonesCheckbox = new CheckboxBuilder()
                .WithLabel(ModLocalization.GetString("debugShowCleanedZones"))
                .WithGreedyState(true)
                .WithCheckedState(_viewModel.ShowCleanedZones)
                .Build();
            cleanedZonesCheckbox.transform.SetParent(rootGo.transform, false);

            CheckboxController dumpButtonCheckbox = new CheckboxBuilder()
                .WithLabel(ModLocalization.GetString("debugShowDumpButton"))
                .WithGreedyState(true)
                .WithCheckedState(_viewModel.ShowDumpButton)
                .Build();
            dumpButtonCheckbox.transform.SetParent(rootGo.transform, false);

            // Parented by the builder itself: the combo hands its floating dropdown and its click trap
            // that same parent, and only reparents them onto the canvas while open.
            ComboController logLevelCombo = new ComboBuilder()
                .WithParent(rootGo.transform)
                .WithLabel(ModLocalization.GetString("debugLogLevel"))
                .WithLabelFor(LogLevels.LabelFor)
                .Build();

            // Last, and not handed to the ContentController: the readout reads the world rather than a
            // setting, so it has nothing to sync back from the view model and drives its own refresh.
            QuadReadoutController quadReadout = new QuadReadoutBuilder()
                .WithLabel(ModLocalization.GetString("debugQuads"))
                .Build();
            quadReadout.transform.SetParent(rootGo.transform, false);

            return rootGo
                .AddComponent<ContentController>()
                .WithViewModel(_viewModel)
                .WithControls(cleanedZonesCheckbox, dumpButtonCheckbox, logLevelCombo);
        }
    }
}
