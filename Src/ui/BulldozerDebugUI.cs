using KSP.UI.Screens;
using UnityEngine;
using com.github.lhervier.ksp.shared;
using com.github.lhervier.ksp.shared.ugui.popup;
using com.github.lhervier.ksp.bulldozermod.settings;
using com.github.lhervier.ksp.bulldozermod.ui.styles;
using com.github.lhervier.ksp.bulldozermod.ui.ugui;

namespace com.github.lhervier.ksp.bulldozermod.ui
{
    /// <summary>
    /// Entry point in flight: the toolbar button and the uGUI window it toggles. The window is driven by a
    /// (shared) PopupController that handles its own lazy spawn, position and open state; we only open and
    /// close it, and react to OnOpenChanged so the toolbar toggle stays in sync — including when KSP closes
    /// the window itself (Escape) or restores it open at scene load.
    ///
    /// Flight only, and that is not a simplification: what the window switches is drawn on terrain that only
    /// exists in flight. A button in the space center would toggle something with nothing to show.
    ///
    /// The settings are loaded here and handed to the view model, which owns the pairing between a stored
    /// preference and the overlay's static switch.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class BulldozerDebugUI : MonoBehaviour
    {
        private static readonly ModLogger LOGGER = new ModLogger("DebugUI");

        private const string DialogId = "BulldozerDebugUGUI";

        private ApplicationLauncherButton _toolbarButton;
        private PopupController _popupController;

        private void Start()
        {
            BulldozerSettings settings = new BulldozerSettings();
            settings.Load();

            // Hosted here rather than on the window, so the state survives KSP destroying it (Escape) and
            // the player reopening it.
            BulldozerDebugViewModel viewModel = gameObject
                .AddComponent<BulldozerDebugViewModel>()
                .WithSettings(settings);

            // The popup controller is a component on THIS GameObject: it survives KSP destroying the window
            // and persists its own open state, so we never track visibility ourselves. Neither a title bar
            // nor an overlay builder: this window shows nothing beside its title, and has no menu and no
            // modal of its own.
            _popupController = new PopupBuilder<MonoBehaviour, ContentController, MonoBehaviour>()
                .WithHost(gameObject)
                .WithPopupID(DialogId)
                .WithTitle(ModLocalization.GetString("windowTitle"))
                .WithIcon(LoadIcon())
                .WithContentBuilder(new ContentBuilder().WithViewModel(viewModel))
                .WithSize(new Vector2(BulldozerPalette.WindowWidth, BulldozerPalette.WindowHeight))
                .Build();

            // The controller restores its own open state (in its Start, after this method returns), so we
            // only subscribe: a restored-open window then syncs the toolbar through this handler.
            if (_popupController != null)
            {
                _popupController.OnOpenChanged.Add(OnPopupOpenChanged);
            }

            GameEvents.onGUIApplicationLauncherReady.Add(OnLauncherReady);
            LOGGER.LogInfo("Started");
        }

        private void OnDestroy()
        {
            GameEvents.onGUIApplicationLauncherReady.Remove(OnLauncherReady);
            RemoveToolbarButton();

            // _popupController is a component on this GO: Unity destroys it with us, and it dismisses a
            // still-open window in its own OnDestroy. We only drop our reference and unsubscribe.
            if (_popupController != null)
            {
                _popupController.OnOpenChanged.Remove(OnPopupOpenChanged);
                _popupController = null;
            }
        }

        // ==========================================================================
        // Toolbar
        // ==========================================================================

        private void OnLauncherReady()
        {
            if (_toolbarButton != null || ApplicationLauncher.Instance == null)
            {
                return;
            }
            try
            {
                _toolbarButton = ApplicationLauncher.Instance.AddModApplication(
                    OnToggleOn,
                    OnToggleOff,
                    null, null, null, null,
                    ApplicationLauncher.AppScenes.FLIGHT,
                    GameDatabase.Instance.GetTexture(Constants.ModName + "/icon", false)
                        ?? Texture2D.whiteTexture);
            }
            catch (System.Exception e)
            {
                LOGGER.LogError("Error creating toolbar button: " + e.Message);
            }

            // The launcher may become ready after the window state was restored at scene load: press the
            // button now to reflect an already-open window (false: no callback).
            if (_toolbarButton != null && _popupController != null && _popupController.IsOpen)
            {
                _toolbarButton.SetTrue(false);
            }
        }

        private void RemoveToolbarButton()
        {
            if (_toolbarButton == null)
            {
                return;
            }
            try
            {
                ApplicationLauncher.Instance.RemoveModApplication(_toolbarButton);
            }
            catch (System.Exception e)
            {
                LOGGER.LogError("Error removing toolbar button: " + e.Message);
            }
            _toolbarButton = null;
        }

        // ==========================================================================
        // Visibility
        // ==========================================================================

        private void OnToggleOn()
        {
            if (_popupController != null) _popupController.Show();
        }

        private void OnToggleOff()
        {
            if (_popupController != null) _popupController.Hide();
        }

        // The window's open state changed (button, ×, Escape, or restore-at-load): keep the toolbar button
        // pressed state in sync. SetTrue/SetFalse(false): do not re-fire the toggle callbacks.
        private void OnPopupOpenChanged()
        {
            if (_toolbarButton == null)
            {
                return;
            }
            if (_popupController != null && _popupController.IsOpen)
            {
                _toolbarButton.SetTrue(false);
            }
            else
            {
                _toolbarButton.SetFalse(false);
            }
        }

        // The mod's toolbar icon, reused as the window's title-bar icon. Null when the texture is missing.
        private static Sprite LoadIcon()
        {
            Texture2D tex = GameDatabase.Instance != null
                ? GameDatabase.Instance.GetTexture(Constants.ModName + "/icon", false)
                : null;
            if (tex == null)
            {
                return null;
            }
            return Sprite.Create(
                tex,
                new Rect(0f, 0f, tex.width, tex.height),
                new Vector2(0.5f, 0.5f),
                100f);
        }
    }
}
