using UnityEngine;
using UnityEngine.UI;
using TMPro;
using com.github.lhervier.ksp.shared.ugui;
using com.github.lhervier.ksp.shared.ugui.styles;
using com.github.lhervier.ksp.mod.bulldozer.ui.styles;

namespace com.github.lhervier.ksp.mod.bulldozer.ui.ugui
{
    /// <summary>
    /// The quads readout section: a caption and, under it, the block of text the controller refreshes.
    /// Takes whatever height the content leaves it, so the rows above keep their natural size.
    /// </summary>
    public class QuadReadoutBuilder : IUGUIBuilder<QuadReadoutController>
    {
        private string _label = string.Empty;
        public QuadReadoutBuilder WithLabel(string label)
        {
            this._label = label ?? string.Empty;
            return this;
        }

        public QuadReadoutController Build()
        {
            var rootGo = new GameObject("QuadReadout", typeof(RectTransform));

            var layout = rootGo.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(0, 0, 0, 0);
            layout.spacing = BulldozerPalette.ReadoutSpacing;
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            // The one row that grows: the readout is the only thing here whose height is not known in
            // advance, so it takes the leftover rather than pushing the window around as lines come and go.
            var rootLayoutElement = rootGo.AddComponent<LayoutElement>();
            rootLayoutElement.flexibleHeight = 1f;
            rootLayoutElement.minHeight = BulldozerPalette.ReadoutMinHeight;

            // Caption, styled like the combo's: same role, one step quieter than a checkbox label.
            var captionGo = new GameObject("Caption", typeof(RectTransform));
            captionGo.transform.SetParent(rootGo.transform, false);

            var caption = UGUILabels.AddLabel(captionGo);
            caption.text = _label;
            caption.fontSize = ComboPalette.LabelFontSize;
            caption.color = ComboPalette.LabelColor;
            caption.alignment = TextAlignmentOptions.Left;

            var textGo = new GameObject("Text", typeof(RectTransform));
            textGo.transform.SetParent(rootGo.transform, false);

            var textLayoutElement = textGo.AddComponent<LayoutElement>();
            textLayoutElement.flexibleHeight = 1f;

            // AddLabel hands back a single overflowing line; this one is a paragraph, anchored to the top
            // so the block does not jump when a line appears or goes. Truncate rather than Overflow: a
            // readout longer than its box must be cut inside the window, not spilled over the rows above.
            var text = UGUILabels.AddLabel(textGo);
            text.fontSize = BulldozerPalette.ReadoutFontSize;
            text.color = DefaultPalette.LabelColor;
            text.alignment = TextAlignmentOptions.TopLeft;
            text.enableWordWrapping = true;
            text.overflowMode = TextOverflowModes.Truncate;

            return rootGo
                .AddComponent<QuadReadoutController>()
                .WithTextComponent(text);
        }
    }
}
