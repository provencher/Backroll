using HouraiTeahouse.Networking;
using System;
using System.Threading;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Assertions;

namespace HouraiTeahouse.Backroll {

public struct BackrollSessionConfig {
   public LobbyMember[] Players;
   public LobbyMember[] Spectators;
   public BackrollSessionCallbacks Callbacks;

   public bool IsValid => GetLobby() != null && Callbacks?.IsValid == true;

   public Lobby GetLobby() {
      Lobby lobby = null;
      if (!(GetLobby(Players, ref lobby) &&
            GetLobby(Spectators, ref lobby))) {
         return null;
      }
      return lobby;
   }

   static bool GetLobby(LobbyMember[] members, ref Lobby lobby) {
      if (members == null) return false;
      foreach (var member in members) {
         if (member == null) return false;
         lobby = lobby ?? member.Lobby;
         if (lobby != member.Lobby) return false;
      }
      return true;
   }
}

public unsafe class P2PBackrollSession<T> : BackrollSession<T> where T : struct {
  const int RECOMMENDATION_INTERVAL           = 240;
  const int DEFAULT_DISCONNECT_TIMEOUT        = 5000;
  const int DEFAULT_DISCONNECT_NOTIFY_START   = 750;

  readonly BackrollConnection[] _players;
  readonly BackrollConnection[] _spectators;
  readonly BackrollConnectStatus[] _localConnectStatus;

  BackrollSessionCallbacks _callbacks;
  Sync                  _sync;

  public bool IsSynchronizing {
    get {
        for (var i = 0; i < _players.Length; i++) {
           if (!_players[i].IsLocal && !_players[i].IsSynchronized &&
               !_localConnectStatus[i].Disconnected) {
              return false;
           }
        }
        for (var i = 0; i < _spectators.Length; i++) {
           if (!_spectators[i].IsLocal && !_spectators[i].IsSynchronized) {
              return false;
           }
        }
        return true;
    }
  }

  int                   _next_recommended_sleep;
  int                   _next_spectator_frame;

  public int PlayerCount => _players.Length;
  public int SpectatorCount => _spectators.Length;

  public P2PBackrollSession(BackrollSessionConfig config) {
     _next_recommended_sleep = 0;

     _next_spectator_frame = 0;

     _callbacks = config.Callbacks;

     // Initialize the synchronziation layer
     _sync = new Sync(_localConnectStatus, new Sync.Config {
        NumPlayers = config.Players.Length,
        InputSize = InputSize,
        Callbacks = _callbacks,
        NumPredictionFrames = BackrollConstants.kMaxPredictionFrames,
     });

      _localConnectStatus = new BackrollConnectStatus[PlayerCount];
     for (int i = 0; i < _localConnectStatus.Length; i++) {
        unchecked {
         _localConnectStatus[i].LastFrame = ~0;
        }
     }

     _players = InitializeConnections(config.Players);
     _spectators = InitializeConnections(config.Spectators);
  }

  BackrollConnection[] InitializeConnections(LobbyMember[] members) {
     Assert.IsNotNull(members);
     var connections = new BackrollConnection[members.Length];
     for (var i = 0; i < connections.Length; i++) {
        var connection = new BackrollConnection(members[i], i, _localConnectStatus);
        SetupConnection(connection);
        connection.SetDisconnectTimeout((uint)DEFAULT_DISCONNECT_TIMEOUT);
        connection.SetDisconnectNotifyStart((uint)DEFAULT_DISCONNECT_NOTIFY_START);
        connection.Synchronize();
        connections[i] = connection;
     }
     return connections;
  }

  void UpdateConnections() {
     foreach (var connection in _players) {
        connection.Update();
     }
     foreach (var connection in _spectators) {
        connection.Update();
     }
  }

  public override void Idle(int timeout) {
     if (_sync.InRollback || IsSynchronizing) return;
     UpdateConnections();
     _sync.CheckSimulation(timeout);

     // notify all of our endpoints of their local frame number for their
     // next connection quality report
     int current_frame = _sync.FrameCount;
     foreach (var connection in _players) {
        connection.SetLocalFrameNumber(current_frame);
     }

     int minFrame;
     if (PlayerCount <= 2) {
        minFrame = Poll2Players(current_frame);
     } else {
        minFrame = PollNPlayers(current_frame);
     }

     Debug.LogFormat("last confirmed frame in p2p backend is {}.", minFrame);
     if (minFrame >= 0) {
        Assert.IsTrue(minFrame != int.MaxValue);
        if (SpectatorCount > 0) {
           while (_next_spectator_frame <= minFrame) {
              Debug.LogFormat("pushing frame {} to spectators.", _next_spectator_frame);

              GameInput input;
              input.Frame = _next_spectator_frame;
              input.Size = (uint)(InputSize * PlayerCount);
              _sync.GetConfirmedInputs(input.bits, InputSize * PlayerCount, _next_spectator_frame);
              foreach (var connection in _spectators) {
                 connection.SendInput(input);
              }
              _next_spectator_frame++;
           }
        }
        Debug.LogFormat("setting confirmed frame in sync to {}.", minFrame);
        _sync.SetLastConfirmedFrame(minFrame);
     }

     // send timesync notifications if now is the proper time
     if (current_frame > _next_recommended_sleep) {
        int interval = 0;
        foreach (var connection in _players) {
           interval = Mathf.Max(interval, connection.RecommendFrameDelay());
        }

        if (interval > 0) {
           _callbacks.OnTimeSync?.Invoke(new TimeSyncEvent { FramesAhead = interval });
           _next_recommended_sleep = current_frame + RECOMMENDATION_INTERVAL;
        }
     }
     // XXX: this is obviously a farce...
     if (timeout != 0) {
        Thread.Sleep(1);
     }
  }

  protected int Poll2Players(int current_frame) {
     // discard confirmed frames as appropriate
     int minFrame = int.MaxValue;
     for (var i = 0; i < _players.Length; i++ ) {
        bool connected = true;
        if (_players[i].IsRunning) {
           int ignore = 0;
           connected = _players[i].GetPeerConnectStatus(i, ref ignore);
        }
        if (!_localConnectStatus[i].Disconnected) {
           minFrame = (int)Math.Min(_localConnectStatus[i].LastFrame, minFrame);
        }
        Debug.LogFormat("  local endp: connected = {}, last_received = {}, minFrame = {}.",
          !_localConnectStatus[i].Disconnected, _localConnectStatus[i].LastFrame,
          minFrame);
        if (!connected && !_localConnectStatus[i].Disconnected) {
           Debug.LogFormat("disconnecting i {} by remote request.", i);
           DisconnectPlayerQueue(i, minFrame);
        }
        Debug.LogFormat("  minFrame = {}.", minFrame);
     }
     return minFrame;
  }

  protected int PollNPlayers(int current_frame) {
     int last_received = 0;

     // discard confirmed frames as appropriate
     int minFrame = Int32.MaxValue;
     for (var queue = 0; queue < PlayerCount; queue++) {
        bool connected = true;
        int minConfirmed = Int32.MaxValue;
        Debug.LogFormat("considering queue {}.", queue);
        for (var i = 0; i < _players.Length; i++) {
           // we're going to do a lot of logic here in consideration of endpoint i.
           // keep accumulating the minimum confirmed point for all n*n packets and
           // throw away the rest.
           if (_players[i].IsRunning) {
              bool peer_connected = _players[i].GetPeerConnectStatus(queue, ref last_received);
              connected = connected && peer_connected;
              minConfirmed = Mathf.Min(last_received, minConfirmed);
              Debug.Log(
                  $"  endpoint {i}: connected = {connected}, last_received = {last_received}, minConfirmed = {minConfirmed}.");
           } else {
              Debug.Log($"  endpoint {i}: ignoring... not running.");
           }
        }
        // merge in our local status only if we're still connected!
        if (!_localConnectStatus[queue].Disconnected) {
           minConfirmed = (int)Math.Min(_localConnectStatus[queue].LastFrame, minConfirmed);
        }
        Debug.LogFormat("  local endp: connected = {}, last_received = {}, minConfirmed = {}.",
            !_localConnectStatus[queue].Disconnected, _localConnectStatus[queue].LastFrame, minConfirmed);

        if (connected) {
           minFrame = Math.Min(minConfirmed, minFrame);
        } else {
           // check to see if this disconnect notification is further back than we've been before.  If
           // so, we need to re-adjust.  This can happen when we detect our own disconnect at frame n
           // and later receive a disconnect notification for frame n-1.
           if (!_localConnectStatus[queue].Disconnected || _localConnectStatus[queue].LastFrame > minConfirmed) {
              Debug.LogFormat("disconnecting queue {} by remote request.", queue);
              DisconnectPlayerQueue(queue, minConfirmed);
           }
        }
        Debug.LogFormat("  minFrame = {}.", minFrame);
     }
     return minFrame;
  }

  public override void AddLocalInput(BackrollPlayerHandle player,
                                     ref T playerInput) {
     int queue;
     GameInput input;

     if (_sync.InRollback) {
        throw new BackrollException(BackrollErrorCode.InRollback);
     }
     if (IsSynchronizing) {
        throw new BackrollException(BackrollErrorCode.NotSynchronized);
     }

     queue = PlayerHandleToQueue(player);
     input = GameInput.Create<T>(GameInput.kNullFrame, ref playerInput);

     // Feed the input for the current frame into the synchronzation layer.
     if (!_sync.AddLocalInput(queue, ref input)) {
       throw new BackrollException(BackrollErrorCode.PredictionThreshold);
     }

     if (input.Frame == GameInput.kNullFrame) return;
     // xxx: <- comment why this is the case
     // Update the local connect status state to indicate that we've got a
     // confirmed local frame for this player.  this must come first so it
     // gets incorporated into the next packet we send.

     Debug.LogFormat("setting local connect status for local queue {} to {}",
         queue, input.Frame);
     _localConnectStatus[queue].LastFrame = input.Frame;

     // Send the input to all the remote players.
     for (int i = 0; i < PlayerCount; i++) {
        if (!_players[i].IsLocal) {
           _players[i].SendInput(input);
        }
     }
  }

  public override int SyncInput(void *values, int size) {
     // Wait until we've started to return inputs.
     if (IsSynchronizing) {
        throw new BackrollException(BackrollErrorCode.NotSynchronized);
     }
     return _sync.SynchronizeInputs(values, size);
  }

  public override void AdvanceFrame() {
     Debug.LogFormat("End of frame ({})...", _sync.FrameCount);
     _sync.IncrementFrame();
     Idle(0);
  }

  static int GetIndex(INetworkReciever reciever, BackrollConnection[] connections) {
    for (var i = 0; i < connections.Length; i++) {
       if ((reciever as LobbyMember) == connections[i].LobbyMember) {
         return i;
       }
    }
    return -1;
  }

  void SetupConnection(BackrollConnection connection) {
     var member = connection.LobbyMember;
     var queue = GetIndex(connection.LobbyMember, _players);
     member.OnDisconnected += () => {
         if (queue >= 0) {
            DisconnectPlayer(QueueToPlayerHandle(queue));
         }
         var spectator = GetIndex(member, _spectators);
         if (spectator >= 0) {
            _spectators[spectator].Disconnect();
         }
     };
     if (queue < 0) return ;
     var handle = QueueToPlayerHandle(queue);
     connection.OnInput += (input) => {
       if (_localConnectStatus[queue].Disconnected) return;
       int current_remote_frame = _localConnectStatus[queue].LastFrame;
       int new_remote_frame = input.Frame;
       Assert.IsTrue(current_remote_frame == -1 || new_remote_frame == (current_remote_frame + 1));

       _sync.AddRemoteInput(queue, ref input);
       // Notify the other endpoints which frame we received from a peer
       Debug.LogFormat("setting remote connect status for queue {} to {}", 
         queue, new_remote_frame); 
       _localConnectStatus[queue].LastFrame = new_remote_frame;
     };

     connection.OnSynchronizing += (total, count) => {
        _callbacks.OnPlayerSynchronizing?.Invoke(new PlayerSynchronizingEvent {
           Player = handle,
           Total = total,
           Count = count
        });
     };

     connection.OnSynchronized += () => {
        _callbacks.OnPlayerSynchronized?.Invoke(new PlayerSynchronizedEvent {
           Player = handle,
        });
     };

     connection.OnNetworkInterrupted += (timeout) => {
        _callbacks.OnConnectionInterrupted?.Invoke(new ConnectionInterruptedEvent {
           Player = handle,
           DisconnectTimeout = timeout,
        });
     };

     connection.OnNetworkResumed += () => {
        _callbacks.OnConnectionResumed?.Invoke(new ConnectionResumedEvent {
           Player = handle
        });
     };
  }

  // Called only as the result of a local decision to disconnect.  The remote
  // decisions to disconnect are a result of us parsing the peer_connect_settings
  // blob in every endpoint periodically.
  public override void DisconnectPlayer(BackrollPlayerHandle player) {
     int queue = PlayerHandleToQueue(player);

     if (_localConnectStatus[queue].Disconnected) {
       throw new BackrollException(BackrollErrorCode.PlayerDisconnected);
     }

     if (_players[queue].IsLocal) {
        int current_frame = _sync.FrameCount;
        // xxx: we should be tracking who the local player is, but for now assume
        // that if the endpoint is not initalized, this must be the local player.
        Debug.LogFormat("Disconnecting local player {} at frame {} by user request.",
            queue, _localConnectStatus[queue].LastFrame);
        for (int i = 0; i < PlayerCount; i++) {
           if (!_players[i].IsLocal) {
              DisconnectPlayerQueue(i, current_frame);
           }
        }
     } else {
        Debug.LogFormat("Disconnecting queue {} at frame {} by user request.",
            queue, _localConnectStatus[queue].LastFrame);
        DisconnectPlayerQueue(queue, _localConnectStatus[queue].LastFrame);
     }
  }

  protected void DisconnectPlayerQueue(int queue, int syncto) {
     int framecount = _sync.FrameCount;

     _players[queue].Disconnect();

     Debug.LogFormat("Changing queue {} local connect status for last frame from {} to {} on disconnect request (current: {}).",
         queue, _localConnectStatus[queue].LastFrame, syncto, framecount);

     _localConnectStatus[queue].Disconnected = true;
     _localConnectStatus[queue].LastFrame = syncto;

     if (syncto < framecount) {
        Debug.LogFormat("adjusting simulation to account for the fact that {} Disconnected @ {}.", queue, syncto);
        _sync.AdjustSimulation(syncto);
        Debug.LogFormat("finished adjusting simulation.");
     }

     _callbacks.OnDisconnected?.Invoke(new DisconnectedEvent {
        Player = QueueToPlayerHandle(queue)
     });
  }

  public override BackrollNetworkStats GetNetworkStats(BackrollPlayerHandle player) {
     return _players[PlayerHandleToQueue(player)].GetNetworkStats();
  }

  public override void SetFrameDelay(BackrollPlayerHandle player, int delay) {
     _sync.SetFrameDelay(PlayerHandleToQueue(player), delay);
  }

  public override void SetDisconnectTimeout(int timeout) {
     foreach (var connection in _players) {
       connection.SetDisconnectTimeout((uint)timeout);
     }
  }

  public override void SetDisconnectNotifyStart(int disconnect_notify_start) {
     foreach (var connection in _players) {
       connection.SetDisconnectNotifyStart((uint)disconnect_notify_start);
     }
  }

  protected int PlayerHandleToQueue(BackrollPlayerHandle player) {
     int offset = ((int)player.Id - 1);
     if (offset < 0 || offset >= PlayerCount) {
        throw new BackrollException(BackrollErrorCode.InvalidPlayerHandle);
     }
     return offset;
  }

  protected BackrollPlayerHandle QueueToPlayerHandle(int queue) => 
    new BackrollPlayerHandle { Id = queue + 1 };

}

}


