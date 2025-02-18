﻿using JetBrains.Annotations;
using KSP.Game;
using KSP.Messages;
using SpaceWarp.API.Game.Messages;

namespace SpaceWarp.Modules;

/// <summary>
/// The module for game-related APIs.
/// </summary>
[UsedImplicitly]
public class Game : SpaceWarpModule
{
    /// <inheritdoc />
    public override string Name => "SpaceWarp.Game";

    /// <inheritdoc />
    public override void InitializeModule()
    {
        var game = GameManager.Instance.Game;
        game.Messages.Subscribe(typeof(GameStateEnteredMessage), StateChanges.OnGameStateEntered, false, true);
        game.Messages.Subscribe(typeof(GameStateLeftMessage), StateChanges.OnGameStateLeft, false, true);
        game.Messages.Subscribe(typeof(GameStateChangedMessage), StateChanges.OnGameStateChanged, false, true);
        game.Messages.Subscribe(typeof(TrackingStationLoadedMessage), StateLoadings.TrackingStationLoadedHandler, false, true);
        game.Messages.Subscribe(typeof(TrackingStationUnloadedMessage), StateLoadings.TrackingStationUnloadedHandler, false, true);
        game.Messages.Subscribe(typeof(TrainingCenterLoadedMessage), StateLoadings.TrainingCenterLoadedHandler, false, true);
    }
}