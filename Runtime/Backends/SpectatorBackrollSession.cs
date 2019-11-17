using System;
using UnityEngine;
using UnityEngine.Assertions;
using Unity.Collections.LowLevel.Unsafe;

namespace HouraiTeahouse.Backroll {

public unsafe class SpectatorBackrollSession<T> : BackrollSession<T> where T : struct {

    public const int kFrameBufferSize = 64;

    readonly BackrollSessionCallbacks _callbacks;
    readonly GameInput[]  _inputs;
    readonly BackrollConnection _host;
    bool                  _synchronizing;
    int                   _num_players;
    int                   _next_input_to_send;

    public SpectatorBackrollSession(BackrollSessionConfig config) {
        _callbacks = config.Callbacks;
        _num_players = config.Players.Length;
        _next_input_to_send = 0;
        _synchronizing = true;

        _inputs = new GameInput[kFrameBufferSize];
        for (var i = 0; i < _inputs.Length; i++) {
            _inputs[i].Frame = -1;
        }

        _host = new BackrollConnection(config.Players[0], 0, null);
        _host.Synchronize();

        _host.OnConnected += () => {
            _callbacks.OnConnected?.Invoke(new ConnectedEvent {
                Player = new BackrollPlayerHandle { Id = 0 }
            });
        };

        _host.OnSynchronizing += (count, total) => {
            _callbacks.OnPlayerSynchronizing?.Invoke(new PlayerSynchronizingEvent{
                Player = new BackrollPlayerHandle { Id = 0 },
                Count = count,
                Total = total
            });
        };
        
        _host.OnSynchronized += () => {
            if (!_synchronizing) return;
            _callbacks.OnPlayerSynchronized?.Invoke(new PlayerSynchronizedEvent{
                Player = new BackrollPlayerHandle { Id = 0 },
            });
            _callbacks.OnReady?.Invoke();
            _synchronizing = false;
        };

        _host.OnNetworkInterrupted += (disconnectTimeout) => {
            _callbacks.OnConnectionInterrupted?.Invoke(new ConnectionInterruptedEvent {
                Player = new BackrollPlayerHandle { Id = 0 },
                DisconnectTimeout = disconnectTimeout
            });
        };
        
        _host.OnNetworkResumed += () => {
            _callbacks.OnConnectionResumed?.Invoke(new ConnectionResumedEvent {
                Player = new BackrollPlayerHandle { Id = 0 },
            });
        };

        _host.OnDisconnected += () => {
            _callbacks.OnDisconnected?.Invoke(new DisconnectedEvent {
                Player = new BackrollPlayerHandle { Id = 0 },
            });
        };

        _host.OnInput += (input) => {
            _host.SetLocalFrameNumber(input.Frame);
            _host.SendInputAck();
            _inputs[input.Frame % _inputs.Length] = input;
        };
    }

    public override void Idle(int timeout) {
    }

    public override int SyncInput(void* values, int size) {
        // Wait until we've started to return inputs.
        if (_synchronizing) {
            throw new BackrollException(BackrollErrorCode.NotSynchronized);
        }

        ref GameInput input = ref _inputs[_next_input_to_send % _inputs.Length];
        if (input.Frame < _next_input_to_send) {
            // Haven't received the input from the host yet.  Wait
            throw new BackrollException(BackrollErrorCode.PredictionThreshold);
        }
        if (input.Frame > _next_input_to_send) {
            // The host is way way way far ahead of the spectator.  How'd this
            // happen?  Anyway, the input we need is gone forever.
            throw new BackrollException(BackrollErrorCode.GeneralFailure);
        }

        Assert.IsTrue(size >= InputSize * _num_players);
        fixed (byte* ptr = input.bits) {
            UnsafeUtility.MemCpy(values, ptr, InputSize * _num_players);
        }
        _next_input_to_send++;
        return 0;
    }

    public override void AddLocalInput(BackrollPlayerHandle player, ref T input) { }

    public override void DisconnectPlayer(BackrollPlayerHandle player) =>
        throw new NotSupportedException();

    public override BackrollNetworkStats GetNetworkStats(BackrollPlayerHandle player) =>
        throw new NotSupportedException();

    public override void SetFrameDelay(BackrollPlayerHandle player, int Frame_delay) =>
        throw new NotSupportedException();

    public override void SetDisconnectNotifyStart(int timeout) =>
        throw new NotSupportedException();

    public override void SetDisconnectTimeout(int timeout) =>
        throw new NotSupportedException();
 
   public override void AdvanceFrame() {
        Debug.Log($"End of Frame ({_next_input_to_send - 1})...");
   }

}

}