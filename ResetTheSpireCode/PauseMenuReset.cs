using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;

namespace ResetTheSpire.ResetTheSpireCode;

/// <summary>
/// Adds a "Reset" entry to the in-run pause menu. Pressing it starts a brand-new run with the
/// same character, ascension level and custom modifiers, but a freshly generated seed.
/// </summary>
internal static class PauseMenuReset
{
    private const string ButtonName = "Reset";

    private static bool _resetting;

    /// <summary>
    /// Reset only makes sense for a local singleplayer run. Daily runs are tied to the day's seed
    /// and the leaderboard, and multiplayer runs cannot be restarted unilaterally.
    /// </summary>
    public static bool CanReset(IRunState? runState)
    {
        if (runState == null)
        {
            return false;
        }

        NetGameType netType = RunManager.Instance.NetService.Type;
        if (netType is not (NetGameType.Singleplayer or NetGameType.Host or NetGameType.Client))
        {
            return false;
        }

        return runState.GameMode is GameMode.Standard or GameMode.Custom or GameMode.Daily;
    }

    /// <summary>
    /// Builds the Reset button by cloning one of the game's pause-menu buttons, so it inherits the
    /// same artwork, hover animation and controller focus behavior without shipping any assets.
    /// </summary>
    public static void Install(NPauseMenu menu)
    {
        try
        {
            Control? container = menu.GetNodeOrNull<Control>("%ButtonContainer");
            if (container == null)
            {
                MainFile.Logger.Warn("Pause menu has no %ButtonContainer; Reset button was not added.");
                return;
            }

            if (container.GetNodeOrNull<NPauseMenuButton>(ButtonName) != null)
            {
                RefreshFocus(menu);
                return;
            }

            NPauseMenuButton? template =
                container.GetNodeOrNull<NPauseMenuButton>("GiveUp") ??
                container.GetNodeOrNull<NPauseMenuButton>("Settings") ??
                container.GetChildren().OfType<NPauseMenuButton>().FirstOrDefault();
            if (template == null)
            {
                MainFile.Logger.Warn("Pause menu has no button to clone; Reset button was not added.");
                return;
            }

            var button = (NPauseMenuButton)template.Duplicate();

            // Keep the game's focus-node ordering explicit after the rename.
            button.Name = ButtonName;

            // Nodes share resources when duplicated. Give the clone its own shader material so
            // hovering Reset does not recolor the button it was cloned from. The hue (red) is
            // inherited from the cloned Give Up button.
            TextureRect? image = button.GetNodeOrNull<TextureRect>("ButtonImage");
            if (image?.Material is ShaderMaterial material)
            {
                image.Material = (ShaderMaterial)material.Duplicate();
            }

            MegaLabel? label = button.GetNodeOrNull<MegaLabel>("Label");
            label?.SetTextAutoSize("Reset");

            button.Connect(
                NClickableControl.SignalName.Released,
                Callable.From<NButton>(_ => OnResetPressed()));

            container.AddChild(button);

            Control? resume = container.GetNodeOrNull<Control>("Resume");
            container.MoveChild(button, resume != null ? resume.GetIndex() + 1 : container.GetChildCount() - 1);

            RefreshFocus(menu);
        }
        catch (Exception e)
        {
            // A broken pause-menu decoration must never prevent the run from continuing.
            MainFile.Logger.Warn($"Could not add the Reset button: {e}");
        }
    }

    /// <summary>
    /// Re-applies visibility and keyboard/gamepad focus neighbors when the pause menu opens.
    /// </summary>
    public static void RefreshVisibility(NPauseMenu menu, IRunState? runState)
    {
        try
        {
            Control? container = menu.GetNodeOrNull<Control>("%ButtonContainer");
            NPauseMenuButton? button = container?.GetNodeOrNull<NPauseMenuButton>(ButtonName);
            if (button == null)
            {
                return;
            }

            button.Visible = CanReset(runState);
            RefreshFocus(menu);
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"Could not refresh the Reset button: {e}");
        }
    }

    /// <summary>
    /// Disables the Reset button alongside the game's own buttons when the menu closes through
    /// "Save and Quit". Without this it would stay bright while every other button dims.
    /// </summary>
    public static void DisableButton(NPauseMenu menu)
    {
        try
        {
            NButton? button = menu
                .GetNodeOrNull<Control>("%ButtonContainer")?
                .GetNodeOrNull<NButton>(ButtonName);
            button?.Disable();
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"Could not disable the Reset button: {e}");
        }
    }

    private static void RefreshFocus(NPauseMenu menu)
    {
        try
        {
            Control? container = menu.GetNodeOrNull<Control>("%ButtonContainer");
            if (container == null)
            {
                return;
            }

            List<NButton> buttons = container.GetChildren().OfType<NButton>().ToList();
            if (buttons.Count == 0)
            {
                return;
            }

            for (int i = 0; i < buttons.Count; i++)
            {
                NButton button = buttons[i];
                button.FocusNeighborLeft = button.GetPath();
                button.FocusNeighborRight = button.GetPath();
                button.FocusNeighborTop = buttons[(i - 1 + buttons.Count) % buttons.Count].GetPath();
                button.FocusNeighborBottom = buttons[(i + 1) % buttons.Count].GetPath();
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"Could not rebuild pause-menu focus neighbors: {e}");
        }
    }

    private static void OnResetPressed()
    {
        if (_resetting || MultiplayerReset.IsInProgress)
        {
            return;
        }

        RunManager manager = RunManager.Instance;
        IRunState? runState = manager.DebugOnlyGetState();
        if (!CanReset(runState))
        {
            MainFile.Logger.Warn("Reset is not available in this game mode.");
            return;
        }

        switch (manager.NetService.Type)
        {
            case NetGameType.Singleplayer:
                ShowConfirmation(
                    "Start over with the same character, ascension and modifiers, but a new seed?\n\n"
                    + "The current run's progress will be lost.",
                    () => TaskHelper.RunSafely(ResetRunAsync()));
                break;

            case NetGameType.Host:
                ShowConfirmation(
                    "Restart this run for every player with the same characters, ascension and modifiers?\n\n"
                    + "Everyone will start again at Act 1. The current run's progress will be lost.",
                    () => TaskHelper.RunSafely(MultiplayerReset.BeginHostResetAsync()));
                break;

            case NetGameType.Client:
                ShowConfirmation(
                    "Ask the host to restart this run for every player?\n\n"
                    + "Everyone will start again at Act 1. The current run's progress will be lost.",
                    MultiplayerReset.RequestHostReset);
                break;
        }
    }

    /// <summary>
    /// Uses the game's own yes/no popup scene. The raw-string overload lets us avoid shipping a
    /// localization table (and therefore a .pck) just for one confirmation message.
    /// </summary>
    private static void ShowConfirmation(string body, Action onConfirm)
    {
        try
        {
            NGenericPopup? popup = NGenericPopup.Create();
            NModalContainer? modals = NModalContainer.Instance;
            if (popup == null || modals == null)
            {
                onConfirm();
                return;
            }

            modals.Add(popup);
            if (!popup.IsInsideTree())
            {
                popup.QueueFreeSafely();
                onConfirm();
                return;
            }

            NVerticalPopup? vertical = popup.GetNodeOrNull<NVerticalPopup>("VerticalPopup");
            if (vertical == null)
            {
                MainFile.Logger.Warn("Confirmation popup scene has no VerticalPopup; resetting immediately.");
                popup.QueueFreeSafely();
                onConfirm();
                return;
            }

            vertical.SetText("Reset Run", body);
            vertical.InitYesButton(
                new LocString("main_menu_ui", "GENERIC_POPUP.confirm"),
                _ => onConfirm());
            vertical.InitNoButton(
                new LocString("main_menu_ui", "GENERIC_POPUP.cancel"),
                _ => { });
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"Could not show the Reset confirmation; resetting immediately: {e}");
            onConfirm();
        }
    }

    /// <summary>
    /// Records the in-progress daily attempt as abandoned (progress, run history and daily score)
    /// before the replacement run starts. <see cref="RunManager.IsAbandoned"/> has a private
    /// setter, so it is set through the property setter that Harmony can resolve.
    /// </summary>
    internal static void RecordAbandonedAttempt(RunManager manager)
    {
        try
        {
            MethodInfo? setter = AccessTools.PropertySetter(typeof(RunManager), nameof(RunManager.IsAbandoned))
                ?? typeof(RunManager)
                    .GetProperty(
                        nameof(RunManager.IsAbandoned),
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?
                    .GetSetMethod(nonPublic: true);
            if (setter == null)
            {
                MainFile.Logger.Warn("Could not mark the daily attempt as abandoned: IsAbandoned setter not found.");
                return;
            }

            setter.Invoke(manager, new object[] { true });
            manager.OnEnded(false);
            MainFile.Logger.Info("Recorded the partial daily attempt as abandoned.");
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"Could not record the partial daily attempt as abandoned: {e}");
        }
    }

    private static async Task ResetRunAsync()
    {
        if (_resetting)
        {
            return;
        }

        _resetting = true;
        try
        {
            RunManager manager = RunManager.Instance;
            RunState? runState = manager.DebugOnlyGetState();
            if (runState == null || !CanReset(runState))
            {
                return;
            }

            if (manager.NetService.Type != NetGameType.Singleplayer)
            {
                MainFile.Logger.Warn("Singleplayer reset routine was called during a multiplayer run; ignoring.");
                return;
            }

            NGame? game = NGame.Instance;
            if (game == null)
            {
                MainFile.Logger.Warn("Cannot reset the run: the game node is not available.");
                return;
            }

            var character = runState.Players[0].Character;
            int ascension = runState.AscensionLevel;
            GameMode gameMode = runState.GameMode;
            bool isDaily = gameMode == GameMode.Daily;
            DateTimeOffset? dailyTime = isDaily ? manager.DailyTime : null;

            string oldSeed = runState.Rng.StringSeed;
            string newSeed;
            List<ActModel> acts;

            if (isDaily)
            {
                // A daily is identified by the day's seed. Restart the same daily at Act 1 so it
                // remains comparable on the leaderboard. The abandoned attempt is recorded below.
                newSeed = oldSeed;
                acts = runState.Acts.Select(a => a.CanonicalInstance).ToList();
            }
            else
            {
                newSeed = SeedHelper.GetRandomSeed();
                while (string.Equals(newSeed, oldSeed, StringComparison.OrdinalIgnoreCase))
                {
                    newSeed = SeedHelper.GetRandomSeed();
                }

                // A reset is a brand-new run, so it starts at Act 1 and rerolls the act sequence
                // from the new seed just like the character-select flow does. For custom runs,
                // keep the Act 1 the player explicitly chose in the lobby.
                UnlockState unlockState = SaveManager.Instance.GenerateUnlockStateFromProgress();
                var actRng = new Rng(StringHelper.GetDeterministicHashCode(newSeed), "act_selection");
                acts = ActModel.GetRandomList(actRng, unlockState, isMultiplayer: false).ToList();
                if (gameMode == GameMode.Custom && acts.Count > 0)
                {
                    acts[0] = runState.Acts[0].CanonicalInstance;
                }
            }

            // Clone modifiers from their serialized form so the new run does not reuse mutable
            // modifier instances that are still bound to the old run state. Standard runs have
            // none unless another mod injected them, in which case they are preserved too.
            List<ModifierModel> modifiers = runState.Modifiers.Count > 0
                ? runState.Modifiers.Select(m => ModifierModel.FromSerializable(m.ToSerializable())).ToList()
                : new List<ModifierModel>();

            MainFile.Logger.Info(
                $"Resetting run ({character.Id.Entry}, ascension {ascension}, {gameMode}, modifiers: {modifiers.Count}). "
                + $"Seed {oldSeed} -> {newSeed}.");

            // Let any in-flight save finish before we tear the run down; otherwise it could write
            // the old save over the new one afterwards.
            Task? pendingSave = SaveManager.Instance.CurrentRunSaveTask;
            if (pendingSave != null)
            {
                try
                {
                    await pendingSave;
                }
                catch (Exception e)
                {
                    MainFile.Logger.Warn($"A pending run save failed before reset: {e}");
                }
            }

            NTransition? transition = game.Transition;
            if (transition != null)
            {
                await transition.FadeOut(0.4f);
            }

            NCapstoneContainer.Instance?.Close();

            // A daily attempt is scored, so record the part we are throwing away as an abandoned
            // run before starting the replacement. This keeps retries visible on the leaderboard.
            if (isDaily)
            {
                RecordAbandonedAttempt(manager);
            }

            manager.IsPaused = false;
            manager.CleanUp();
            SaveManager.Instance.DeleteCurrentRun();

            // Drop the old run scene now instead of leaving it in the tree while the next run
            // loads. Otherwise its _Process would briefly run against the new RunManager state.
            game.RootSceneContainer.SetCurrentScene(new Control { Name = "ResetPlaceholder" });

            await game.StartNewSingleplayerRun(
                character,
                true,
                acts,
                modifiers,
                newSeed,
                gameMode,
                ascension,
                dailyTime);

            MainFile.Logger.Info($"New run started with seed {newSeed}.");
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed to reset the run: {e}");

            // The old run is already gone at this point; leave the player somewhere usable.
            try
            {
                if (RunManager.Instance.DebugOnlyGetState() == null && NGame.Instance != null)
                {
                    await NGame.Instance.ReturnToMainMenu();
                }
            }
            catch (Exception inner)
            {
                MainFile.Logger.Error($"Could not return to the main menu after a failed reset: {inner}");
            }
        }
        finally
        {
            _resetting = false;
        }
    }
}

[HarmonyPatch(typeof(NPauseMenu), nameof(NPauseMenu._Ready))]
internal static class NPauseMenuReadyPatch
{
    private static void Postfix(NPauseMenu __instance)
    {
        PauseMenuReset.Install(__instance);
    }
}

[HarmonyPatch(typeof(NPauseMenu), nameof(NPauseMenu.Initialize))]
internal static class NPauseMenuInitializePatch
{
    private static void Postfix(NPauseMenu __instance, IRunState runState)
    {
        PauseMenuReset.RefreshVisibility(__instance, runState);
    }
}

/// <summary>
/// "Save and Quit" disables each pause-menu button by name. The Reset button is added at runtime,
/// so it has to be disabled here as well or it stays bright while everything else dims.
/// </summary>
[HarmonyPatch(typeof(NPauseMenu), "CloseToMenu")]
internal static class NPauseMenuCloseToMenuPatch
{
    private static void Prefix(NPauseMenu __instance)
    {
        PauseMenuReset.DisableButton(__instance);
    }
}
