using UnityEngine;
using TMPro;
using com.github.lhervier.ksp.bulldozermod.diagnostics;

namespace com.github.lhervier.ksp.bulldozermod.ui.ugui
{
    /// <summary>
    /// Keeps the quads readout up to date while it is on screen.
    ///
    /// It only runs while the window is shown: the popup controller deactivates the whole window
    /// GameObject when it hides it, which stops this Update with it. That is the whole point of hosting
    /// the readout here — as a standalone IMGUI overlay it paid Unity's OnGUI several times per frame for
    /// the entire flight, whether or not anyone had switched it on.
    /// </summary>
    public class QuadReadoutController : MonoBehaviour
    {
        // Twice a second: the rate the blades' PAW already refreshes its pass cost at, and fast enough to
        // read a spread that changes as the rover creeps towards a quad edge.
        private const float RefreshInterval = 0.5f;

        private TextMeshProUGUI _text;
        public QuadReadoutController WithTextComponent(TextMeshProUGUI text)
        {
            this._text = text;
            return this;
        }

        private readonly QuadReadout _readout = new QuadReadout();
        private float _nextRefresh;

        // Reopening the window must show the state of the world now, not the last text it was left with.
        public void OnEnable()
        {
            _nextRefresh = 0f;
        }

        public void Update()
        {
            if (_text == null || Time.time < _nextRefresh)
            {
                return;
            }
            _nextRefresh = Time.time + RefreshInterval;
            _text.text = _readout.Build();
        }
    }
}
