using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;
using Expr = System.Linq.Expressions.Expression;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.Unlocks;

namespace ResetTheSpire.ResetTheSpireCode;

/// <summary>
/// Host-authoritative run reset for multiplayer sessions.
///
/// Protocol:
///  1. A host or client presses Reset (a client's press is a request to the host).
///  2. The host broadcasts <see cref="ResetRunPrepareMessage"/> with the complete new run config.
///  3. Each client tears down its old run, then replies <see cref="ResetRunReadyMessage"/>.
///  4. The host tears down its old run, creates the new run state, and broadcasts
///     <see cref="ResetRunGoMessage"/>.
///  5. Every peer starts the new run from the same seed/acts/modifiers/characters.
///
/// Tearing down the old run normally disconnects the network session. During a reset the host's
/// <see cref="RunManager.CleanUp"/> is allowed to run in full, but the actual
/// <c>INetGameService.Disconnect</c> call is suppressed so the same session can host the new run.
/// </summary>
internal static class MultiplayerReset
{
    private const float HostReadyTimeoutSeconds = 15f;
    private const float ClientGoTimeoutSeconds = 20f;

    private static readonly HashSet<ulong> _readyReceived = new();
    private static readonly MessageHandlerDelegate<ResetRunPrepareMessage> PrepareHandler = HandlePrepare;
    private static readonly MessageHandlerDelegate<ResetRunReadyMessage> ReadyHandler = HandleReady;
    private static readonly MessageHandlerDelegate<ResetRunGoMessage> GoHandler = HandleGo;
    private static readonly MessageHandlerDelegate<ResetRunRequestMessage> RequestHandler = HandleResetRequest;

    private static INetGameService? _registeredService;
    private static INetGameService? _suspendedService;
    private static bool _inProgress;
    private static bool _waitingForGo;
    private static bool _goReceived;

    public static bool IsInProgress => _inProgress;

    /// <summary>True while the run teardown must not close the shared network session.</summary>
    public static bool IsKeepingServiceAlive(INetGameService? service)
    {
        return service != null && ReferenceEquals(_suspendedService, service);
    }

    public static void EnsureHandlers(RunManager manager)
    {
        try
        {
            INetGameService net = manager.NetService;
            if (net == null || !net.Type.IsMultiplayer())
            {
                return;
            }

            if (ReferenceEquals(_registeredService, net))
            {
                return;
            }

            if (_registeredService != null)
            {
                TryUnregister(_registeredService);
            }

            net.RegisterMessageHandler(PrepareHandler);
            net.RegisterMessageHandler(ReadyHandler);
            net.RegisterMessageHandler(GoHandler);
            net.RegisterMessageHandler(RequestHandler);
            _registeredService = net;

            MainFile.Logger.Info($"Multiplayer reset messages registered for a {net.Type} session.");
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"Could not register multiplayer reset messages: {e}");
        }
    }

    public static void UnregisterHandlers(RunManager manager)
    {
        if (_registeredService == null)
        {
            return;
        }

        try
        {
            TryUnregister(_registeredService);
        }
        finally
        {
            _registeredService = null;
        }
    }

    /// <summary>Client-side: ask the host to restart the run for everyone.</summary>
    public static void RequestHostReset()
    {
        try
        {
            RunManager manager = RunManager.Instance;
            if (manager.NetService.Type != NetGameType.Client)
            {
                return;
            }

            if (_inProgress)
            {
                return;
            }

            MainFile.Logger.Info("Requesting that the host reset this run.");
            manager.NetService.SendMessage(default(ResetRunRequestMessage));
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"Could not send the reset request: {e}");
        }
    }

    /// <summary>Host-side: prepare clients, tear down, and start the replacement run.</summary>
    public static async Task BeginHostResetAsync()
    {
        if (_inProgress)
        {
            return;
        }

        RunManager manager = RunManager.Instance;
        RunState? state = manager.DebugOnlyGetState();
        if (state == null || manager.NetService.Type != NetGameType.Host || !PauseMenuReset.CanReset(state))
        {
            return;
        }

        _inProgress = true;
        try
        {
            ResetPlan? plan = BuildHostPlan(manager, state);
            if (plan == null)
            {
                MainFile.Logger.Error("Could not build the multiplayer reset plan; aborting.");
                return;
            }

            List<ulong> expectedClients = GetClients(manager, state);
            _readyReceived.Clear();

            MainFile.Logger.Info(
                $"Multiplayer reset starting (seed {plan.Seed}, {expectedClients.Count} client(s)).");

            ResetRunPrepareMessage prepare = ToMessage(plan);
            foreach (ulong clientId in expectedClients)
            {
                manager.NetService.SendMessage(prepare, clientId);
            }

            if (!await WaitForReadyAsync(manager, expectedClients))
            {
                throw new TimeoutException("Not every connected client acknowledged the reset.");
            }

            ulong localId = manager.NetService.NetId;
            if (plan.Players.Any(p => p.id != localId
                    && (manager.RunLobby == null
                        || !manager.RunLobby.Players.Any(lp => lp.id == p.id))))
            {
                throw new InvalidOperationException(
                    "A player disconnected while preparing the reset; aborting.");
            }

            await ExecuteHostAsync(manager, plan);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Multiplayer reset failed: {e}");
            await AbortToMainMenuAsync();
        }
        finally
        {
            _inProgress = false;
            _readyReceived.Clear();
        }
    }

    private static async Task ExecuteHostAsync(RunManager manager, ResetPlan plan)
    {
        NGame? game = NGame.Instance;
        if (game == null)
        {
            throw new InvalidOperationException("NGame is not available.");
        }

        if (game.Transition != null)
        {
            await game.Transition.FadeOut(0.4f);
        }

        NCapstoneContainer.Instance?.Close();

        await WaitForPendingSaveAsync();

        if (plan.IsDaily)
        {
            PauseMenuReset.RecordAbandonedAttempt(manager);
        }

        manager.IsPaused = false;
        _suspendedService = manager.NetService;
        try
        {
            manager.CleanUp();
        }
        finally
        {
            _suspendedService = null;
        }

        DisposeLeftoverSynchronizers(manager);
        ResetLoadingState(manager.NetService);
        SaveManager.Instance.DeleteCurrentMultiplayerRun();
        game.RootSceneContainer.SetCurrentScene(new Control { Name = "ResetPlaceholder" });

        await StartRunWithPlanAsync(plan, sendGo: true);
        MainFile.Logger.Info($"Multiplayer run restarted with seed {plan.Seed}.");
    }

    private static async Task PrepareClientAsync(ResetPlan plan)
    {
        try
        {
            NGame? game = NGame.Instance;
            if (game == null)
            {
                _inProgress = false;
                return;
            }

            RunManager manager = RunManager.Instance;
            INetGameService net = manager.NetService;

            if (game.Transition != null)
            {
                await game.Transition.FadeOut(0.4f);
            }

            NCapstoneContainer.Instance?.Close();

            await WaitForPendingSaveAsync();

            if (plan.IsDaily)
            {
                PauseMenuReset.RecordAbandonedAttempt(manager);
            }

            manager.IsPaused = false;
            _suspendedService = net;
            try
            {
                manager.CleanUp();
            }
            finally
            {
                _suspendedService = null;
            }

            DisposeLeftoverSynchronizers(manager);
            ResetLoadingState(net);
            game.RootSceneContainer.SetCurrentScene(new Control { Name = "ResetPlaceholder" });

            // Mark waiting before signalling Ready, so an immediate Go cannot be missed.
            _goReceived = false;
            _waitingForGo = true;
            net.SendMessage(default(ResetRunReadyMessage));
            MainFile.Logger.Info("Prepared for the host's reset; waiting for the start signal.");

            float waited = 0f;
            while (!_goReceived && waited < ClientGoTimeoutSeconds)
            {
                PumpNetwork(net);
                if (_goReceived)
                {
                    break;
                }

                if (!net.IsConnected)
                {
                    MainFile.Logger.Warn("Network disconnected while waiting for the reset start signal.");
                    break;
                }

                await game.AwaitProcessFrame();
                waited += (float)game.GetProcessDeltaTime();
            }

            _waitingForGo = false;
            if (!_goReceived)
            {
                MainFile.Logger.Warn("Timed out waiting for the host's reset start signal.");
                await AbortToMainMenuAsync();
                return;
            }

            await StartRunWithPlanAsync(plan, sendGo: false);
            MainFile.Logger.Info($"Multiplayer run restarted with seed {plan.Seed}.");
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Preparing for the host's reset failed: {e}");
            _waitingForGo = false;
            await AbortToMainMenuAsync();
        }
        finally
        {
            _inProgress = false;
            _goReceived = false;
        }
    }

    private static async Task StartRunWithPlanAsync(ResetPlan plan, bool sendGo)
    {
        NGame? game = NGame.Instance;
        if (game == null)
        {
            throw new InvalidOperationException("NGame is not available.");
        }

        RunManager manager = RunManager.Instance;
        INetGameService net = manager.NetService;
        StartRunLobby? lobby = null;

        try
        {
            lobby = new StartRunLobby(
                plan.GameMode,
                net,
                NoopStartRunLobbyListener.Instance,
                Math.Max(4, plan.Players.Count));
            lobby.Players.AddRange(plan.Players);

            // The run's input synchronizer is replaced by the lobby's, and the remote cursor
            // container must follow it (Initialize disposes the previous synchronizer).
            game.RemoteCursorContainer.Initialize(lobby.InputSynchronizer, plan.Players.Select(p => p.id));

            Task<RunState> startTask = game.StartNewMultiplayerRun(
                lobby,
                true,
                plan.Acts,
                plan.Modifiers,
                plan.Seed,
                plan.Ascension,
                plan.DailyTime);

            if (sendGo)
            {
                if (manager.DebugOnlyGetState() == null || startTask.IsFaulted || startTask.IsCanceled)
                {
                    try
                    {
                        await startTask;
                    }
                    catch
                    {
                        // The real failure is reported below.
                    }

                    throw new InvalidOperationException("Run setup failed; clients were not told to start.");
                }

                foreach (StartRunLobbyPlayer player in plan.Players)
                {
                    if (player.id != net.NetId)
                    {
                        net.SendMessage(default(ResetRunGoMessage), player.id);
                    }
                }
            }

            await startTask;
        }
        finally
        {
            if (lobby != null)
            {
                DetachLobbyHandlers(lobby);
                lobby.CleanUp(disconnectSession: false);

                if (manager.DebugOnlyGetState() == null)
                {
                    // Setup failed before the run took ownership of the synchronizer.
                    lobby.InputSynchronizer.Dispose();
                }
            }
        }
    }

    private static ResetPlan? BuildHostPlan(RunManager manager, RunState state)
    {
        try
        {
            bool isDaily = state.GameMode == GameMode.Daily;
            string oldSeed = state.Rng.StringSeed;
            string seed;
            List<ActModel> acts;

            if (isDaily)
            {
                // Same daily, same seed and act lineup; only the progress is reset.
                seed = oldSeed;
                acts = state.Acts.Select(a => a.CanonicalInstance).ToList();
            }
            else
            {
                seed = SeedHelper.GetRandomSeed();
                while (string.Equals(seed, oldSeed, StringComparison.OrdinalIgnoreCase))
                {
                    seed = SeedHelper.GetRandomSeed();
                }

                UnlockState unlockState = state.UnlockState;
                var actRng = new Rng(StringHelper.GetDeterministicHashCode(seed), "act_selection");
                acts = ActModel.GetRandomList(actRng, unlockState, isMultiplayer: true).ToList();

                if (state.GameMode == GameMode.Custom && acts.Count > 0)
                {
                    acts[0] = state.Acts[0].CanonicalInstance;
                }
            }

            List<ModifierModel> modifiers = state.Modifiers
                .Select(m => ModifierModel.FromSerializable(m.ToSerializable()))
                .ToList();

            var players = new List<StartRunLobbyPlayer>(state.Players.Count);

            if (manager.RunLobby != null)
            {
                // RunState.Players keeps players who have disconnected; RunLobby.Players only has
                // the ones still connected to this session, which is what the new run should use.
                foreach (RunLobbyPlayer lobbyPlayer in manager.RunLobby.Players)
                {
                    Player? player = state.Players.FirstOrDefault(p => p.NetId == lobbyPlayer.id);
                    if (player == null)
                    {
                        continue;
                    }

                    players.Add(new StartRunLobbyPlayer
                    {
                        id = player.NetId,
                        slotId = players.Count,
                        character = player.Character,
                        unlockState = player.UnlockState.ToSerializable(),
                        maxMultiplayerAscensionUnlocked = state.AscensionLevel,
                        isModded = lobbyPlayer.isModded,
                        isReady = true,
                    });
                }
            }
            else
            {
                foreach (Player player in state.Players)
                {
                    players.Add(new StartRunLobbyPlayer
                    {
                        id = player.NetId,
                        slotId = players.Count,
                        character = player.Character,
                        unlockState = player.UnlockState.ToSerializable(),
                        maxMultiplayerAscensionUnlocked = state.AscensionLevel,
                        isModded = manager.NetService.LocalVersion.IsModded(),
                        isReady = true,
                    });
                }
            }

            if (players.Count == 0)
            {
                MainFile.Logger.Error("No connected players were found while building the reset plan.");
                return null;
            }

            return new ResetPlan
            {
                Players = players,
                Acts = acts,
                Modifiers = modifiers,
                Seed = seed,
                Ascension = state.AscensionLevel,
                GameMode = state.GameMode,
                DailyTime = isDaily ? manager.DailyTime : null,
            };
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Could not build the multiplayer reset plan: {e}");
            return null;
        }
    }

    private static ResetPlan? PlanFromMessage(ResetRunPrepareMessage message)
    {
        try
        {
            if (message.players == null || message.actIds == null || message.modifiers == null)
            {
                MainFile.Logger.Warn("Received an incomplete multiplayer reset plan.");
                return null;
            }

            if (string.IsNullOrEmpty(message.seed))
            {
                MainFile.Logger.Warn("Received a multiplayer reset plan with an empty seed.");
                return null;
            }

            var acts = new List<ActModel>(message.actIds.Count);
            foreach (string actId in message.actIds)
            {
                acts.Add(ModelDb.GetById<ActModel>(ModelId.Deserialize(actId)));
            }

            List<ModifierModel> modifiers = message.modifiers
                .Select(ModifierModel.FromSerializable)
                .ToList();

            return new ResetPlan
            {
                Players = message.players.ToList(),
                Acts = acts,
                Modifiers = modifiers,
                Seed = message.seed,
                Ascension = message.ascension,
                GameMode = message.gameMode,
                DailyTime = message.hasDailyTime
                    ? DateTimeOffset.FromUnixTimeSeconds(message.dailyTimeUnix)
                    : null,
            };
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Could not read the multiplayer reset plan: {e}");
            return null;
        }
    }

    private static ResetRunPrepareMessage ToMessage(ResetPlan plan)
    {
        return new ResetRunPrepareMessage
        {
            players = plan.Players.ToList(),
            actIds = plan.Acts.Select(a => a.Id.ToString()).ToList(),
            modifiers = plan.Modifiers.Select(m => m.ToSerializable()).ToList(),
            seed = plan.Seed,
            ascension = plan.Ascension,
            gameMode = plan.GameMode,
            hasDailyTime = plan.DailyTime.HasValue,
            dailyTimeUnix = plan.DailyTime?.ToUnixTimeSeconds() ?? 0L,
        };
    }

    private static List<ulong> GetClients(RunManager manager, RunState state)
    {
        ulong localId = manager.NetService.NetId;
        if (manager.RunLobby != null)
        {
            return manager.RunLobby.Players
                .Select(p => p.id)
                .Where(id => id != localId)
                .ToList();
        }

        return state.Players
            .Select(p => p.NetId)
            .Where(id => id != localId)
            .ToList();
    }

    private static async Task<bool> WaitForReadyAsync(RunManager manager, List<ulong> expectedClients)
    {
        if (expectedClients.Count == 0)
        {
            return true;
        }

        NGame? game = NGame.Instance;
        if (game == null)
        {
            return false;
        }

        INetGameService net = manager.NetService;
        float waited = 0f;
        while (waited < HostReadyTimeoutSeconds)
        {
            List<ulong> stillConnected = manager.RunLobby == null
                ? expectedClients
                : expectedClients
                    .Where(id => manager.RunLobby.Players.Any(p => p.id == id))
                    .ToList();

            if (stillConnected.All(id => _readyReceived.Contains(id)))
            {
                return true;
            }

            PumpNetwork(net);
            if (!net.IsConnected)
            {
                MainFile.Logger.Warn("Network disconnected while waiting for reset-ready replies.");
                return false;
            }

            await game.AwaitProcessFrame();
            waited += (float)game.GetProcessDeltaTime();
        }

        List<ulong> missing = expectedClients
            .Where(id => !_readyReceived.Contains(id))
            .Where(id => manager.RunLobby == null || manager.RunLobby.Players.Any(p => p.id == id))
            .ToList();

        if (missing.Count > 0)
        {
            MainFile.Logger.Warn(
                $"Reset aborted: no ready response from {string.Join(",", missing)}.");
            return false;
        }

        return true;
    }

    private static void HandleResetRequest(ResetRunRequestMessage message, ulong senderId)
    {
        try
        {
            RunManager manager = RunManager.Instance;
            if (manager.NetService.Type != NetGameType.Host || _inProgress)
            {
                return;
            }

            RunState? state = manager.DebugOnlyGetState();
            if (state == null || !state.Players.Any(p => p.NetId == senderId))
            {
                MainFile.Logger.Warn($"Ignoring reset request from unknown player {senderId}.");
                return;
            }

            MainFile.Logger.Info($"Player {senderId} requested a multiplayer run reset.");
            TaskHelper.RunSafely(BeginHostResetAsync());
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"Could not handle the reset request: {e}");
        }
    }

    private static void HandlePrepare(ResetRunPrepareMessage message, ulong senderId)
    {
        try
        {
            RunManager manager = RunManager.Instance;
            if (manager.NetService.Type != NetGameType.Client || _inProgress)
            {
                return;
            }

            if (manager.DebugOnlyGetState() == null)
            {
                return;
            }

            ResetPlan? plan = PlanFromMessage(message);
            if (plan == null)
            {
                return;
            }

            _inProgress = true;
            _goReceived = false;
            _waitingForGo = false;

            MainFile.Logger.Info($"Host requested a run reset (seed {plan.Seed}); preparing.");
            TaskHelper.RunSafely(PrepareClientAsync(plan));
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Could not handle the reset prepare message: {e}");
            _inProgress = false;
        }
    }

    private static void HandleReady(ResetRunReadyMessage message, ulong senderId)
    {
        if (!_inProgress)
        {
            return;
        }

        if (RunManager.Instance.NetService.Type != NetGameType.Host)
        {
            return;
        }

        MainFile.Logger.Info($"Player {senderId} is ready for the reset.");
        _readyReceived.Add(senderId);
    }

    private static void HandleGo(ResetRunGoMessage message, ulong senderId)
    {
        if (RunManager.Instance.NetService.Type != NetGameType.Client)
        {
            return;
        }

        if (!_waitingForGo)
        {
            MainFile.Logger.Warn("Received a reset start signal before the client was prepared; ignoring.");
            return;
        }

        _goReceived = true;
    }

    private static void PumpNetwork(INetGameService net)
    {
        try
        {
            if (net.IsConnected)
            {
                net.Update();
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"Network update failed during reset: {e}");
        }
    }

    /// <summary>
    /// RunManager.CleanUp disposes most synchronizers, but relies on the socket being closed for a
    /// few of them. Because a reset keeps the session alive, dispose the remaining net handlers
    /// explicitly so the old run cannot react to messages sent by the new run.
    /// </summary>
    private static void DisposeLeftoverSynchronizers(RunManager manager)
    {
        try
        {
            manager.CombatStateSynchronizer?.Dispose();
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"Could not dispose the old combat synchronizer: {e}");
        }

        try
        {
            manager.EventSynchronizer?.Dispose();
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"Could not dispose the old event synchronizer: {e}");
        }

        try
        {
            manager.OneOffSynchronizer?.Dispose();
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"Could not dispose the old one-off synchronizer: {e}");
        }
    }

    private static void ResetLoadingState(INetGameService net)
    {
        try
        {
            NetLoadingHandle.Release(net);
            net.SetGameLoading(false);
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"Could not clear the old run's loading state: {e}");
        }
    }

    private static async Task WaitForPendingSaveAsync()
    {
        Task? pendingSave = SaveManager.Instance.CurrentRunSaveTask;
        if (pendingSave == null)
        {
            return;
        }

        try
        {
            await pendingSave;
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"A pending run save failed before the multiplayer reset: {e}");
        }
    }

    private static async Task AbortToMainMenuAsync()
    {
        try
        {
            RunManager manager = RunManager.Instance;
            INetGameService net = manager.NetService;
            if (manager.DebugOnlyGetState() == null && net != null && net.IsConnected)
            {
                // CleanUp() was already run, so it will not disconnect for us.
                net.Disconnect(NetError.Quit);
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"Could not close the network session after a failed reset: {e}");
        }

        try
        {
            if (NGame.Instance != null)
            {
                await NGame.Instance.ReturnToMainMenu();
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Could not return to the main menu after a failed reset: {e}");
        }
    }

    private static void TryUnregister(INetGameService net)
    {
        try
        {
            net.UnregisterMessageHandler(PrepareHandler);
            net.UnregisterMessageHandler(ReadyHandler);
            net.UnregisterMessageHandler(GoHandler);
            net.UnregisterMessageHandler(RequestHandler);
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"Could not unregister multiplayer reset messages: {e}");
        }
    }

    /// <summary>
    /// StartRunLobby.CleanUp leaves several lobby-only message handlers registered (the base game
    /// leaks them on every run start too). Since a reset can be repeated many times in one session,
    /// explicitly detach every remaining Handle* handler after the run has started.
    /// </summary>
    private static void DetachLobbyHandlers(StartRunLobby lobby)
    {
        try
        {
            INetGameService net = RunManager.Instance.NetService;
            MethodInfo? genericUnregister = typeof(INetGameService)
                .GetMethods()
                .FirstOrDefault(m =>
                    m.Name == nameof(INetGameService.UnregisterMessageHandler)
                    && m.IsGenericMethodDefinition);
            if (genericUnregister == null)
            {
                return;
            }

            foreach (MethodInfo method in typeof(StartRunLobby).GetMethods(
                         BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != 2
                    || parameters[1].ParameterType != typeof(ulong)
                    || !method.Name.StartsWith("Handle", StringComparison.Ordinal))
                {
                    continue;
                }

                Type messageType = parameters[0].ParameterType;
                if (!typeof(INetMessage).IsAssignableFrom(messageType))
                {
                    continue;
                }

                Type delegateType = typeof(MessageHandlerDelegate<>).MakeGenericType(messageType);
                Delegate handler;
                try
                {
                    // Build the delegate through an expression tree so private handlers in the
                    // game assembly can be bound without visibility restrictions.
                    ParameterExpression messageParameter = Expr.Parameter(messageType, "message");
                    ParameterExpression senderParameter = Expr.Parameter(typeof(ulong), "senderId");
                    MethodCallExpression call = Expr.Call(
                        Expr.Constant(lobby),
                        method,
                        messageParameter,
                        senderParameter);
                    handler = Expr.Lambda(delegateType, call, messageParameter, senderParameter).Compile();
                }
                catch
                {
                    continue;
                }

                try
                {
                    genericUnregister
                        .MakeGenericMethod(messageType)
                        .Invoke(net, new object[] { handler });
                }
                catch
                {
                    // Best effort: a missed handler is dormant during normal play.
                }
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"Could not detach leftover lobby handlers: {e}");
        }
    }

    private sealed class ResetPlan
    {
        public required List<StartRunLobbyPlayer> Players { get; init; }

        public required List<ActModel> Acts { get; init; }

        public required List<ModifierModel> Modifiers { get; init; }

        public required string Seed { get; init; }

        public required int Ascension { get; init; }

        public required GameMode GameMode { get; init; }

        public DateTimeOffset? DailyTime { get; init; }

        public bool IsDaily => GameMode == GameMode.Daily;
    }

    private sealed class NoopStartRunLobbyListener : IStartRunLobbyListener
    {
        public static readonly NoopStartRunLobbyListener Instance = new();

        public void PlayerConnected(StartRunLobbyPlayer player)
        {
        }

        public void PlayerChanged(StartRunLobbyPlayer player, bool isRandomCharacterResolution)
        {
        }

        public void AscensionChanged()
        {
        }

        public void SeedChanged()
        {
        }

        public void ModifiersChanged()
        {
        }

        public void MaxAscensionChanged()
        {
        }

        public void RemotePlayerDisconnected(StartRunLobbyPlayer player)
        {
        }

        public void BeginRun(string seed, List<ActModel> acts, IReadOnlyList<ModifierModel> modifiers)
        {
        }

        public void LocalPlayerDisconnected(NetErrorInfo info)
        {
        }
    }
}

/// <summary>Host -> clients: the complete configuration of the replacement run.</summary>
public struct ResetRunPrepareMessage : INetMessage, IPacketSerializable
{
    public List<StartRunLobbyPlayer>? players;
    public List<string>? actIds;
    public List<SerializableModifier>? modifiers;
    public string? seed;
    public int ascension;
    public GameMode gameMode;
    public bool hasDailyTime;
    public long dailyTimeUnix;

    public bool ShouldBroadcast => false;

    public NetTransferMode Mode => NetTransferMode.Reliable;

    public LogLevel LogLevel => LogLevel.Info;

    public bool ShouldBuffer => false;

    public void Serialize(PacketWriter writer)
    {
        if (players == null || actIds == null || modifiers == null || seed == null)
        {
            throw new InvalidOperationException("Tried to serialize an incomplete reset plan.");
        }

        writer.WriteList(players, 3);
        writer.WriteInt(actIds.Count);
        foreach (string actId in actIds)
        {
            writer.WriteString(actId);
        }

        writer.WriteList(modifiers);
        writer.WriteString(seed);
        writer.WriteInt(ascension);
        writer.WriteEnum(gameMode);
        writer.WriteBool(hasDailyTime);
        writer.WriteLong(dailyTimeUnix);
    }

    public void Deserialize(PacketReader reader)
    {
        players = reader.ReadList<StartRunLobbyPlayer>(3);

        int actCount = reader.ReadInt();
        actIds = new List<string>(actCount);
        for (int i = 0; i < actCount; i++)
        {
            actIds.Add(reader.ReadString());
        }

        modifiers = reader.ReadList<SerializableModifier>();
        seed = reader.ReadString();
        ascension = reader.ReadInt();
        gameMode = reader.ReadEnum<GameMode>();
        hasDailyTime = reader.ReadBool();
        dailyTimeUnix = reader.ReadLong();
    }
}

/// <summary>Client -> host: this client is prepared for the reset and will wait for Go.</summary>
public struct ResetRunReadyMessage : INetMessage, IPacketSerializable
{
    public bool ShouldBroadcast => false;

    public NetTransferMode Mode => NetTransferMode.Reliable;

    public LogLevel LogLevel => LogLevel.Info;

    public bool ShouldBuffer => false;

    public void Serialize(PacketWriter writer)
    {
    }

    public void Deserialize(PacketReader reader)
    {
    }
}

/// <summary>Host -> clients: begin the replacement run now.</summary>
public struct ResetRunGoMessage : INetMessage, IPacketSerializable
{
    public bool ShouldBroadcast => false;

    public NetTransferMode Mode => NetTransferMode.Reliable;

    public LogLevel LogLevel => LogLevel.Info;

    public bool ShouldBuffer => false;

    public void Serialize(PacketWriter writer)
    {
    }

    public void Deserialize(PacketReader reader)
    {
    }
}

/// <summary>Client -> host: ask the host to reset the run.</summary>
public struct ResetRunRequestMessage : INetMessage, IPacketSerializable
{
    public bool ShouldBroadcast => false;

    public NetTransferMode Mode => NetTransferMode.Reliable;

    public LogLevel LogLevel => LogLevel.Info;

    public bool ShouldBuffer => false;

    public void Serialize(PacketWriter writer)
    {
    }

    public void Deserialize(PacketReader reader)
    {
    }
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.Launch))]
internal static class RunManagerLaunchPatch
{
    private static void Postfix(RunManager __instance)
    {
        MultiplayerReset.EnsureHandlers(__instance);
    }
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.CleanUp))]
internal static class RunManagerCleanUpPatch
{
    private static void Prefix(RunManager __instance)
    {
        if (!MultiplayerReset.IsKeepingServiceAlive(__instance.NetService))
        {
            MultiplayerReset.UnregisterHandlers(__instance);
        }
    }
}

/// <summary>
/// Suppresses the session-closing call made by <see cref="RunManager.CleanUp"/> while a
/// multiplayer reset is in progress. The same connection is reused for the replacement run.
/// </summary>
[HarmonyPatch(typeof(NetHostGameService), nameof(NetHostGameService.Disconnect))]
internal static class NetHostDisconnectPatch
{
    private static bool Prefix(NetHostGameService __instance)
    {
        return !MultiplayerReset.IsKeepingServiceAlive(__instance);
    }
}

[HarmonyPatch(typeof(NetClientGameService), nameof(NetClientGameService.Disconnect))]
internal static class NetClientDisconnectPatch
{
    private static bool Prefix(NetClientGameService __instance)
    {
        return !MultiplayerReset.IsKeepingServiceAlive(__instance);
    }
}




