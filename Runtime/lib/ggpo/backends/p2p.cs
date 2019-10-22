using HouraiTeahouse.Networking;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Assertions;

namespace HouraiTeahouse.Backroll {

public struct BackrollSessionConfig {
   public LobbyMember[] Players;
   public LobbyMember[] Spectators;

   public bool IsValid => GetLobby() != null;

   public LobbyBase GetLobby() {
      LobbyBase lobby = null;
      if (!(GetLobby(Players, ref lobby) && 
            GetLobby(Spectators, ref lobby))) {
         return null;
      }
      return lobby;
   }

   static bool GetLobby(LobbyMember[] members, ref LobbyBase lobby) {
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
  readonly BackrollConnectStatus[] _local_connect_status;

  BackrollSessionCallbacks _callbacks;
//   Poll                  _poll;
  Sync                  _sync;

  bool                  _synchronizing;
  int                   _next_recommended_sleep;

  int                   _next_spectator_frame;
  int                   _disconnect_timeout;
  int                   _disconnect_notify_start;

  public int PlayerCount => _players.Length;
  public int SpectatorCount => _spectators.Length;

  public P2PBackrollSession(BackrollSessionConfig config) : base(config.GetLobby()) { 
     _next_recommended_sleep = 0;
     
     _disconnect_timeout = DEFAULT_DISCONNECT_TIMEOUT;
     _disconnect_notify_start = DEFAULT_DISCONNECT_NOTIFY_START;
     _next_spectator_frame = 0;

     // Initialize the synchronziation layer
     _sync = new Sync(_local_connect_status, new Sync.Config {
        NumPlayers = config.Players.Length,
        InputSize = InputSize,
        Callbacks = _callbacks,
        NumPredictionFrames = BackrollConstants.kMaxPredictionFrames,
     });

     _synchronizing = true;
     _players = InitializeConnections(config.Players);
     _spectators = InitializeConnections(config.Spectators);

      _local_connect_status = new BackrollConnectStatus[PlayerCount];
     for (int i = 0; i < _local_connect_status.Length; i++) {
        unchecked {
         _local_connect_status[i].LastFrame = (uint)~0;
        }
     }
  }

  BackrollConnection[] InitializeConnections(LobbyMember[] members) {
     Assert.IsNotNull(members);
     var connections = new BackrollConnection[members.Length];
     for (var i = 0; i < connections.Length; i++) {
        var connection = new BackrollConnection(members[i]);
        connection.SetDisconnectTimeout((uint)_disconnect_timeout); 
        connection.SetDisconnectNotifyStart((uint)_disconnect_notify_start);
        connection.Synchronize();
        connections[i] = connection;
     }
     return connections;
  }

  public override void Idle(int timeout) {
     if (!_sync.InRollback) {
        _poll.Pump(0);

        PollUdpProtocolEvents();

        if (!_synchronizing) {
           _sync.CheckSimulation(timeout);

           // notify all of our endpoints of their local frame number for their
           // next connection quality report
           int current_frame = _sync.GetFrameCount();
           foreach (var connection in _players) {
              connection.SetLocalFrameNumber(current_frame);
           }

           int total_min_confirmed;
           if (PlayerCount <= 2) {
              total_min_confirmed = Poll2Players(current_frame);
           } else {
              total_min_confirmed = PollNPlayers(current_frame);
           }

           Debug.LogFormat("last confirmed frame in p2p backend is {}.", total_min_confirmed);
           if (total_min_confirmed >= 0) {
              Assert.IsTrue(total_min_confirmed != int.MaxValue);
              if (SpectatorCount > 0) {
                 while (_next_spectator_frame <= total_min_confirmed) {
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
              Debug.LogFormat("setting confirmed frame in sync to {}.", total_min_confirmed);
              _sync.SetLastConfirmedFrame(total_min_confirmed);
           }

           // send timesync notifications if now is the proper time
           if (current_frame > _next_recommended_sleep) {
              int interval = 0;
              foreach (var connection in _players) {
                 interval = Mathf.Max(interval, connection.RecommendFrameDelay());
              }

              if (interval > 0) {
                 GGPOEvent info;
                 info.code = GGPO_EVENTCODE_TIMESYNC;
                 info.u.timesync.frames_ahead = interval;
                 _callbacks.on_event(&info);
                 _next_recommended_sleep = current_frame + RECOMMENDATION_INTERVAL;
              }
           }
           // XXX: this is obviously a farce...
           if (timeout != 0) {
              Thread.Sleep(1);
           }
        }
     }
  }

  protected int Poll2Players(int current_frame) {
     // discard confirmed frames as appropriate
     int total_min_confirmed = int.MaxValue;
     for (var i = 0; i < _players.Length; i++ ) {
        bool queue_connected = true;
        if (_players[i].IsRunning) {
           int ignore;
           queue_connected = _players[i].GetPeerConnectStatus(i, &ignore);
        }
        if (!_local_connect_status[i].Disconnected) {
           total_min_confirmed = (int)Math.Min(_local_connect_status[i].LastFrame, total_min_confirmed);
        }
        Debug.LogFormat("  local endp: connected = {}, last_received = {}, total_min_confirmed = {}.", 
          !_local_connect_status[i].Disconnected, _local_connect_status[i].LastFrame, 
          total_min_confirmed);
        if (!queue_connected && !_local_connect_status[i].Disconnected) {
           Debug.LogFormat("disconnecting i {} by remote request.", i);
           DisconnectPlayerQueue(i, total_min_confirmed);
        }
        Debug.LogFormat("  total_min_confirmed = {}.", total_min_confirmed);
     }
     return total_min_confirmed;
  }

  protected int PollNPlayers(int current_frame) {
     int i, queue, last_received;

     // discard confirmed frames as appropriate
     int total_min_confirmed = Int32.MaxValue;
     for (queue = 0; queue < PlayerCount; queue++) {
        bool queue_connected = true;
        int queue_min_confirmed = Int32.MaxValue;
        Debug.LogFormat("considering queue {}.", queue);
        for (var i = 0; i < _players.Length; i++) {
           // we're going to do a lot of logic here in consideration of endpoint i.
           // keep accumulating the minimum confirmed point for all n*n packets and
           // throw away the rest.
           if (_players[i].IsRunning) {
              bool connected = _players[i].GetPeerConnectStatus(queue, &last_received);

              queue_connected = queue_connected && connected;
              queue_min_confirmed = Mathf.Min(last_received, queue_min_confirmed);
              Debug.LogFormat("  endpoint {}: connected = {}, last_received = {}, queue_min_confirmed = {}.", i, connected, last_received, queue_min_confirmed);
           } else {
              Debug.LogFormat("  endpoint {}: ignoring... not running.", i);
           }
        }
        // merge in our local status only if we're still connected!
        if (!_local_connect_status[queue].Disconnected) {
           queue_min_confirmed = Math.Min(_local_connect_status[queue].LastFrame, queue_min_confirmed);
        }
        Debug.LogFormat("  local endp: connected = {}, last_received = {}, queue_min_confirmed = {}.", !_local_connect_status[queue].Disconnected, _local_connect_status[queue].LastFrame, queue_min_confirmed);

        if (queue_connected) {
           total_min_confirmed = Math.Min(queue_min_confirmed, total_min_confirmed);
        } else {
           // check to see if this disconnect notification is further back than we've been before.  If
           // so, we need to re-adjust.  This can happen when we detect our own disconnect at frame n
           // and later receive a disconnect notification for frame n-1.
           if (!_local_connect_status[queue].Disconnected || _local_connect_status[queue].LastFrame > queue_min_confirmed) {
              Debug.LogFormat("disconnecting queue {} by remote request.", queue);
              DisconnectPlayerQueue(queue, queue_min_confirmed);
           }
        }
        Debug.LogFormat("  total_min_confirmed = {}.", total_min_confirmed);
     }
     return total_min_confirmed;
  }

  public void AddLocalInput(BackrollPlayerHandle player, void *values, int size) {
     int queue;
     GameInput input;

     if (_sync.InRollback) {
        throw new BackrollException(BackrollErrorCode.InRollback);
     }
     if (_synchronizing) {
        throw new BackrollException(BackrollErrorCode.NotSynchronized);
     }

     queue = PlayerHandleToQueue(player);
     input = new GameInput(GameInput.kNullFrame, values, (uint)size);

     // Feed the input for the current frame into the synchronzation layer.
     if (!_sync.AddLocalInput(queue, ref input)) {
        return GGPO_ERRORCODE_PREDICTION_THRESHOLD;
     }

     if (input.frame != GameInput.kNullFrame) { // xxx: <- comment why this is the case
        // Update the local connect status state to indicate that we've got a
        // confirmed local frame for this player.  this must come first so it
        // gets incorporated into the next packet we send.

        Debug.Log("setting local connect status for local queue {} to {}", queue, input.frame);
        _local_connect_status[queue].LastFrame = input.frame;

        // Send the input to all the remote players.
        for (int i = 0; i < PlayerCount; i++) {
           if (_players[i].IsInitialized) {
              _players[i].SendInput(input);
           }
        }
     }
  }

  public override int SyncInput(void *values, int size) {
     // Wait until we've started to return inputs.
     if (_synchronizing) {
        throw new BackrollException(BackrollErrorCode.NotSynchronized);
     }
     return _sync.SynchronizeInputs(values, size);
  }

  public override void AdvanceFrame() {
     Debug.Log("End of frame ({})...", _sync.GetFrameCount());
     _sync.IncrementFrame();
     DoPoll(0);
     PollSyncEvents();
  }

  protected void PollSyncEvents() {
     Sync.Event e;
     while (_sync.GetEvent(e)) {
        OnSyncEvent(e);
     }
     return;
  }

  static bool InMembers(INetworkReciever reciever, BackrollConnection[] connections) {
     foreach (var connection in connections) {
        if ((reciever as LobbyMember) == connection.LobbyMember) {
           return true;
        }
     }
     return false;
  }

  bool IsPlayer(INetworkReciever reciever) => InMembers(reciever, _players);
  bool IsSpectator(INetworkReciever reciever) => InMembers(reciever, _spectators);

  protected override void InitConnection(LobbyMember member) {
     base.InitConnection(member);

     var connection = new BackrollConnection(member);
     member.OnInput += (input) => {
      if (!IsPlayer(member)) return;
      if (_local_connect_status[queue].Disconnected) return;
      int current_remote_frame = _local_connect_status[queue].LastFrame;
      int new_remote_frame = msg.Input.Frame;
      Assert.IsTrue(current_remote_frame == -1 || new_remote_frame == (current_remote_frame + 1));

      _sync.AddRemoteInput(queue, evt.u.Input);
      // Notify the other endpoints which frame we received from a peer
      Debug.Log("setting remote connect status for queue {} to {}", queue, new_remote_frame);
      _local_connect_status[queue].LastFrame = new_remote_frame;
     };

     member.OnSynchronizing += (total, count) => {
        OnSynchronizing?.Invoke(member, total, count);
     };

     member.OnSynchronized += () => {
        OnSynchronized?.Invoke(member);
        CheckInitialSync();
     };

     member.OnNetworkInterrupted += (disconnect_timeout) => {
        OnNetworkInterrupted?.Invoke(member, disconnect_timeout);
     };

     member.OnNetworkResumed += () => {
        OnNetworkResumed?.Invoke(member);
     };
  }

  protected override void DestroyConnection(LobbyMember member) {
    base.DestoryConnection(member);
    if (IsPlayer(member)) {
      DisconnectPlayer(QueueToPlayerHandle(queue));
      _players.Remove(member);
    }
    if (IsSpectator(member)) {
      _spectators[member].Disconnect();
      _spectators.Remove(member);
    }
  }

  // Called only as the result of a local decision to disconnect.  The remote
  // decisions to disconnect are a result of us parsing the peer_connect_settings
  // blob in every endpoint periodically.
  public override void DisconnectPlayer(BackrollPlayerHandle player) {
     GGPOErrorCode result;
     int queue = PlayerHandleToQueue(player);
     if (!GGPO_SUCCEEDED(result)) {
        return result;
     }

     if (_local_connect_status[queue].Disconnected) {
        return GGPO_ERRORCODE_PLAYER_DISCONNECTED;
     }

     if (!_players[queue].IsInitialized()) {
        int current_frame = _sync.GetFrameCount();
        // xxx: we should be tracking who the local player is, but for now assume
        // that if the endpoint is not initalized, this must be the local player.
        Debug.Log("Disconnecting local player {} at frame {} by user request.", queue, _local_connect_status[queue].LastFrame);
        for (int i = 0; i < PlayerCount; i++) {
           if (_players[i].IsInitialized()) {
              DisconnectPlayerQueue(i, current_frame);
           }
        }
     } else {
        Debug.Log("Disconnecting queue {} at frame {} by user request.", queue, _local_connect_status[queue].LastFrame);
        DisconnectPlayerQueue(queue, _local_connect_status[queue].LastFrame);
     }
     return GGPO_OK;
  }

  protected void DisconnectPlayerQueue(int queue, int syncto) {
     GGPOEvent info;
     int framecount = _sync.GetFrameCount();

     _players[queue].Disconnect();

     Debug.LogFormat("Changing queue {} local connect status for last frame from {} to {} on disconnect request (current: {}).",
         queue, _local_connect_status[queue].LastFrame, syncto, framecount);

     _local_connect_status[queue].Disconnected = true;
     _local_connect_status[queue].LastFrame = syncto;

     if (syncto < framecount) {
        Debug.LogFormat("adjusting simulation to account for the fact that {} Disconnected @ {}.", queue, syncto);
        _sync.AdjustSimulation(syncto);
        Debug.LogFormat("finished adjusting simulation.");
     }

     info.code = GGPO_EVENTCODE_DISCONNECTED_FROM_PEER;
     info.u.Disconnected.player = QueueToPlayerHandle(queue);
     _callbacks.on_event(&info);

     CheckInitialSync();
  }

  public override BackrollNetworkStats GetNetworkStats(BackrollPlayerHandle player) {
     int queue = PlayerHandleToQueue(player);
     return _players[queue].GetNetworkStats();
  }

  public override void SetFrameDelay(BackrollPlayerHandle player, int delay) {
     int queue = PlayerHandleToQueue(player);
     _sync.SetFrameDelay(queue, delay);
  }

  public override void SetDisconnectTimeout(int timeout) {
     _disconnect_timeout = timeout;
     foreach (var connection in _players) {
        if (connection.IsInitialized) {
           connection.SetDisconnectTimeout((uint)_disconnect_timeout);
        }
     }
  }

  public override void SetDisconnectNotifyStart(int timeout) {
     _disconnect_notify_start = timeout;
     foreach (var connection in _players) {
        if (connection.IsInitialized) {
           connection.SetDisconnectNotifyStart((uint)_disconnect_notify_start);
        }
     }
  }

  protected int PlayerHandleToQueue(BackrollPlayerHandle player) {
     int offset = ((int)player.Id - 1);
     if (offset < 0 || offset >= PlayerCount) {
        throw new BackrollException(BackrollErrorCode.InvalidPlayerHandle);
     }
     return offset;
  }

//   public virtual void OnMsg(sockaddr_in &from, UdpMsg *msg, int len) {
//      for (int i = 0; i < PlayerCount; i++) {
//         if (_players[i].HandlesMsg(from, msg)) {
//            _players[i].OnMsg(msg, len);
//            return;
//         }
//      }
//      for (int i = 0; i < SpectatorCount; i++) {
//         if (_spectators[i].HandlesMsg(from, msg)) {
//            _spectators[i].OnMsg(msg, len);
//            return;
//         }
//      }
//   }

  protected void CheckInitialSync() {
     if (_synchronizing) {
        // Check to see if everyone is now synchronized.  If so,
        // go ahead and tell the client that we're ok to accept input.
        for (var i = 0; i < _players.Length; i++) {
           if (_players[i].IsInitialized && !_players[i].IsSynchronized &&
               !_local_connect_status[i].Disconnected) {
              return;
           }
        }
        for (var i = 0; i < _spectators.Length; i++) {
           if (_spectators[i].IsInitialized && !_spectators[i].IsSynchronized) {
              return;
           }
        }

        GGPOEvent info;
        info.code = GGPO_EVENTCODE_RUNNING;
        _callbacks.on_event(&info);
        _synchronizing = false;
     }
  }

}

}


