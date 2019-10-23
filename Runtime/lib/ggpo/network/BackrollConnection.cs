using System;
using HouraiTeahouse.Networking;
using UnityEngine;
using UnityEngine.Assertions;

namespace HouraiTeahouse.Backroll {

public struct BackrollConnectStatus : INetworkSerializable {
   uint data;

   public bool Disconnected {
      get => (data & 1) != 0;
      set => data = (data & ~1) | (uint)(value ? 1 : 0);
   }
   public uint LastFrame {
      get => data << 1;
      set => data = (data & 1) | (uint)(value << 1);
   }

  public void Serialize(ref Serializer serializer) =>
     serializer.Write(data);
  public void Deserialize(ref Deserializer deserializer) =>
     data = deserializer.ReadUInt32();
}

public unsafe struct InputMessage : INetworkSerializable {

   public fixed uint                  connect_status[BackrollConstants.kMaxPlayers];
   public uint                        StartFrame;

   public int                         disconnect_requested;
   public int                         ack_frame;

   public ushort                      NumBits;
   public uint                        InputSize; // XXX: shouldn't be in every single packet!
   public fixed byte                  bits[4096]; /* must be last */

  public void Serialize(ref Serializer serializer) {
     fixed (uint* status = connect_status) {
      for (var i = 0; i < BackrollConstants.kMaxPlayers; i++) {
         // Doable since BackrollConnectionStatus is exactly the size of
         // a uint32.
         ((BackrollConnectStatus*)status)[i].Serialize(ref serializer);
      }
     }
     serializer.Write(StartFrame);
     serializer.Write(disconnect_requested);
     serializer.Write(ack_frame);
     serializer.Write(InputSize);
     fixed (byte* ptr = bits) {
        serializer.Write(ptr, InputSize);
     }
  }

  public void Deserialize(ref Deserializer deserializer) {
     fixed (uint* status = connect_status) {
      for (var i = 0; i < BackrollConstants.kMaxPlayers; i++) {
         // Doable since BackrollConnectionStatus is exactly the size of
         // a uint32.
         ((BackrollConnectStatus*)status)[i].Deserialize(ref deserializer);
      }
     }
     StartFrame = deserializer.ReadUInt32();
     disconnect_requested = deserializer.ReadInt32();
     ack_frame = deserializer.ReadInt32();
     InputSize = deserializer.ReadUInt16();
     fixed (byte* ptr = bits) {
        deserializer.ReadBytes(ptr, InputSize);
     }
  }
}

public struct SyncRequestMessage : INetworkSerializable {
  public uint RandomRequest;
  public byte RemoteEndpoint;

  public void Serialize(ref Serializer serializer) {
    serializer.Write(RandomRequest);
    serializer.Write(RemoteEndpoint);
  }
  public void Deserialize(ref Deserializer deserializer) {
    RandomRequest = deserializer.ReadUInt32();
    RemoteEndpoint = deserializer.ReadByte();
  }
}

public struct SyncReplyMessage : INetworkSerializable {
  public uint RandomReply;

  public void Serialize(ref Serializer serializer) =>
    serializer.Write(RandomReply);
  public void Deserialize(ref Deserializer deserializer) =>
    RandomReply = deserializer.ReadUInt32();
}

public struct KeepAliveMessage : INetworkSerializable {
  public void Serialize(ref Serializer serializer) {}
  public void Deserialize(ref Deserializer deserializer) {}
}

public struct InputAckMessage : INetworkSerializable {
  public int AckFrame;

  public void Serialize(ref Serializer serializer) =>
    serializer.Write(AckFrame);
  public void Deserialize(ref Deserializer deserializer) =>
    AckFrame = deserializer.ReadInt32();
}

public struct QualityReportMessage : INetworkSerializable {
  public byte FrameAdvantage;
  public uint Ping;

  public void Serialize(ref Serializer serializer)  {
    serializer.Write(FrameAdvantage);
    serializer.Write(Ping);
  }

  public void Deserialize(ref Deserializer deserializer) {
    FrameAdvantage = deserializer.ReadByte();
    Ping = deserializer.ReadUInt32();
  }
}

public struct QualityReplyMessage : INetworkSerializable {
  public uint Pong;

  public void Serialize(ref Serializer serializer) =>
    serializer.Write(Pong);
  public void Deserialize(ref Deserializer deserializer) =>
    Pong = deserializer.ReadUInt32();
}

public class BackrollConnection : IDisposable {

  const int UDP_HEADER_SIZE = 28;     /* Size of IP + UDP headers */
  const int NUM_SYNC_PACKETS = 5;
  const int SYNC_RETRY_INTERVAL = 2000;
  const int SYNC_FIRST_RETRY_INTERVAL = 500;
  const int RUNNING_RETRY_INTERVAL = 200;
  const int KEEP_ALIVE_INTERVAL    = 200;
  const int QUALITY_REPORT_INTERVAL = 1000;
  const int NETWORK_STATS_INTERVAL  = 1000;
  const int MAX_SEQ_DISTANCE = (1 << 15);

  enum State {
    Syncing,
    Synchronzied,
    Running,
    Disconnected,
  }

  public enum MessageCodes : byte {
    Input             = 1,
    InputAck          = 2,
    QualityReport     = 3,
    QualityReply      = 4,
    KeepAlive         = 5,
  }

  struct SyncState {
    public uint RoundTripsRemaining;
    public uint Random;
  }

  public LobbyMember LobbyMember { get; }
  State          _current_state;
  SyncState      _sync_state;
  readonly MessageHandlers _messageHandlers;

   // Stats
   int            _roundTripTime;
   int            _packets_sent;
   int            _bytes_sent;
   int            _kbps_sent;
   uint            _stats_start_time;

   // Fairness.
   int               _local_frame_advantage;
   int               _remoteFrameAdvantage;

   // Packet loss...
   RingBuffer<GameInput>      _pending_output;
   GameInput                  _lastRecievedInput;
   GameInput                  _lastSentInput;
   GameInput                  _last_acked_input;
   uint                       _last_send_time;
   uint                       _last_recv_time;
   uint                       _shutdown_timeout;
   uint                       _disconnect_event_sent;
   uint                       _disconnect_timeout;
   uint                       _disconnect_notify_start;
   bool                       _disconnect_notify_sent;

   ushort                     _next_send_seq;
   ushort                     _next_recv_seq;

   // Rift synchronization.
   TimeSync                   _timesync;

   BackrollConnectionStatus[] _peerConnectStatus;

   public bool IsLocal => LobbyMember.Id == LobbyMember.Lobby.UserId;

   public event Action OnConnected;
   public event Action<int /*total*/, int /*count*/> OnSynchronizing;
   public event Action OnSynchronized;
   public event Action<GameInput> OnInput;
   public event Action OnDisconnected;
   public event Action<int /*disconnect_timeout*/> OnNetworkInterrupted;
   public event Action OnNetworkResumed;

  public BackrollConnection(LobbyMember lobbyMember) {
    if (lobbyMember == null) {
      throw new ArgumentNullException(lobbyMember);
    }
    LobbyMember = lobbyMember;

    _pending_output = new RingBuffer<GameInput>(64);
    _peerConnectStatus = new BackrollConnectionStatus[BackrollConstants.kMaxPlayers];
    for (var i = 0; i < _peerConnectStatus.Length; i++) {
      unchecked {
        _peerConnectStatus[i].LastFrame = (uint)-1;
      }
    }

    _messageHandlers = new MessageHandlers();
    _messageHandlers.RegisterHandler((byte)MessageCodes.Input, OnInputAck);
    _messageHandlers.RegisterHandler((byte)MessageCodes.InputAck, OnInputAck);
    _messageHandlers.RegisterHandler((byte)MessageCodes.QualityReport, OnQualityReport);
    _messageHandlers.RegisterHandler((byte)MessageCodes.QualityReply, OnQualityReply);
    _messageHandlers.RegisterHandler((byte)MessageCodes.KeepAlive, OnKeepAlive);
    _messageHandlers.Listen(LobbyMember);
  }

  public void Dispose() => _messageHandlers.Dispose();

   public void Synchronize() {
     if (LobbyMember != null) {
        _current_state = State.Syncing;
        _sync_state.RoundTripsRemaining = NUM_SYNC_PACKETS;
        SendSyncRequest();
     }
  }

  public bool GetPeerConnectStatus(int id, ref int frame) {
    frame = _peerConnectStatus[id].LastFrame;
    return !_peerConnectStatus[id].Disconnect;
  }

   public bool IsSynchronized => _current_state == State.Running;
   public bool IsRunning => _current_state == State.Running;

   public void SendSyncRequest() {
     Send(new SyncRequestMessage { AckFrame = _lastRecievedInput.Frame });
   }

   public void SendInput(in GameInput input) {
      if (_current_state == State.Running) {
         // Check to see if this is a good time to adjust for the rift...
         _timesync.AdvanceFrame(input, _local_frame_advantage, _remoteFrameAdvantage);

         // Save this input packet
         //
         // XXX: This queue may fill up for spectators who do not ack input packets in a timely
         // manner.  When this happens, we can either resize the queue (ug) or disconnect them
         // better, but still ug).  For the meantime, make this queue really big to decrease
         // the odds of this happening...
         _pending_output.Push(input);
      }
      SendPendingOutput();
   }

   public void SendInputAck() {
     Send(new InputAckMessage { AckFrame = _lastRecievedInput.Frame });
   }

   unsafe void Send<T>(in T msg, Reliabilty reliabilty = Reliabilty.Reliable) where T : struct, INetworkSerializable {
     var buffer = stackalloc byte[2048];
     var serializer = Serializer.Create(buffer, (uint)SerializationConstants.kMaxMessageSize);
     _messageHandlers.Serialize<T>(msg, ref serializer);

     _packets_sent++;
     _last_send_time = GetTime();
     _bytes_sent += serializer.Position;

     LobbyMember.SendMessage(serializer.ToArray(), serializer.Position,
                              reliabilty);
   }

   uint GetTime() => (uint)Mathf.FloorToInt(Time.realtimeSinceStartup * 1000);

   public void Disconnect() {
     _current_state = Disconnected;
   }

  public BackrollNetworkStats GetNetworkStats() {
     return new BackrollNetworkStats {
        Ping = _roundTripTime,
        SendQueueLength = _pending_output.Size,
        KbpsSent = _kbps_sent,
        RemoteFramesBehind = _remoteFrameAdvantage,
        LocalFramesBehind = _local_frame_advantage,
     };
  }

  bool OnQualityReply(INetworkReciever _, QualityReplyMessage msg) {
     _roundTripTime = (int)GetTime() - (int)msg.Pong;
     return true;
  }

  bool OnKeepAlive(INetworkReciever _, KeepAliveMessage msg) {
     return true;
  }

  public void SetLocalFrameNumber(int num) {
     // Estimate which frame the other guy is one by looking at the
     // last frame they gave us plus some delta for the one-way packet
     // trip time.
     int remoteFrame = _lastRecievedInput.Frame + (_roundTripTime * 60 / 1000);

     // Our frame advantage is how many frames *behind* the other guy
     // we are.  Counter-intuative, I know.  It's an advantage because
     // it means they'll have to predict more often and our moves will
     // Pop more frequenetly.
     _local_frame_advantage = remoteFrame - localFrame;
   }

   public int RecommendFrameDelay() =>
     _timesync.RecommendFrameWaitDuration(false);

   public void SetDisconnectTimeout(uint timeout) => _disconnect_timeout = timeout;
   public void SetDisconnectNotifyStart(uint timeout) => _disconnect_notify_start = timeout;

   protected void SendPendingOutput() {
     Assert.IsTrue((GAMEINPUT_MAX_BYTES * GAMEINPUT_MAX_PLAYERS * 8) <
                   (1 << BITVECTOR_NIBBLE_SIZE));
     int offset = 0;
     byte* bits;
     GameInput last;
     var msg = new InputMessage();
     fixed (byte* ptr = msg.bits,
            lastPtr = last.bits,
            connect_status = msg.peer_connect_status) {
       if (_pending_output.Size <= 0) {
          msg.StartFrame = 0;
          msg.InputSize = 0;
       } else {
          last = _last_acked_input;

          msg.StartFrame = _pending_output.Peek().Frame;
          msg.InputSize = _pending_output.Peek().Size;

          Assert.IsTrue(last.Frame == -1 || last.Frame + 1 == msg.StartFrame);
          for (var j = 0; j < _pending_output.Size; j++) {
             ref GameInput current = ref _pending_output[j];
             fixed (byte* currentPtr = current.bits) {
               if (UnsafeUtility.MemCmp(currentPtr, lastPtr, current.size) != 0) {
                  for (var i = 0; i < current.Size * 8; i++) {
                     Assert.IsTrue(i < (1 << BitVector.kNibbleSize));
                     if (current[i] == last[i]) continue;
                     BitVector.SetBit(ptr, ref offset);
                     if (current[i]) {
                       BitVector.SetBit(ptr, ref offset);
                     } else {
                       BitVector.ClearBit(ptr, ref offset);
                     }
                     BitVector.WriteNibblet(ptr, i, ref offset);
                  }
               }
             }
             BitVector.ClearBit(ptr, ref offset);
             last = _lastSentInput = current;
          }
       }
       msg.AckFrame = _lastRecievedInput.Frame;
       msg.NumBits = offset;

       msg.DisconnectRequested = _current_state == State.Disconnected;
       if (_localConnectStatus) {
          memcpy(msg->u.input.peer_connect_status, _localConnectStatus, sizeof(UdpMsg::connect_status) * UDP_MSG_MAX_PLAYERS);
       } else {
          memset(msg->u.input.peer_connect_status, 0, sizeof(UdpMsg::connect_status) * UDP_MSG_MAX_PLAYERS);
       }
     }

     Assert.IsTrue(offset < MAX_COMPRESSED_BITS);

     SendMsg(msg);
   }

  bool OnLoopPoll(object cookie) {
    if (LobbyMember == null) return;


   switch (_current_state) {
   case Syncing:
      uint now = timeGetTime();
      uint next_interval = (_state.sync.roundtrips_remaining == NUM_SYNC_PACKETS) ? SYNC_FIRST_RETRY_INTERVAL : SYNC_RETRY_INTERVAL;
      if (_last_send_time && _last_send_time + next_interval < now) {
         Log("No luck syncing after %d ms... Resending sync packet.\n", next_interval);
         SendSyncRequest();
      }
      break;

   case Running:
      // xxx: rig all this up with a timer wrapper
      if (!_state.running.last_input_packet_recv_time || _state.running.last_input_packet_recv_time + RUNNING_RETRY_INTERVAL < now) {
         Log("Haven't exchanged packets in a while (last received:%d  last sent:%d).  Resending.\n", _last_received_input.frame, _last_sent_input.frame);
         SendPendingOutput();
         _state.running.last_input_packet_recv_time = now;
      }

      if (!_state.running.last_quality_report_time || _state.running.last_quality_report_time + QUALITY_REPORT_INTERVAL < now) {
         UdpMsg *msg = new UdpMsg(UdpMsg::QualityReport);
         OnQualityReport?.Invoke(, _local_frame_advantage);
         msg->u.quality_report.ping = timeGetTime();
         msg->u.quality_report.frame_advantage = _local_frame_advantage;
         SendMsg(msg);
         _state.running.last_quality_report_time = now;
      }

      if (!_state.running.last_network_stats_interval || _state.running.last_network_stats_interval + NETWORK_STATS_INTERVAL < now) {
         UpdateNetworkStats();
         _state.running.last_network_stats_interval =  now;
      }

      if (_last_send_time && _last_send_time + KEEP_ALIVE_INTERVAL < now) {
         Log("Sending keep alive packet\n");
         SendMsg(new UdpMsg(UdpMsg::KeepAlive));
      }

      if (_disconnect_timeout && _disconnect_notify_start &&
         !_disconnect_notify_sent && (_last_recv_time + _disconnect_notify_start < now)) {
         Log("Endpoint has stopped receiving packets for %d ms.  Sending notification.\n", _disconnect_notify_start);
         Event e(Event::NetworkInterrupted);
         e.u.network_interrupted.disconnect_timeout = _disconnect_timeout - _disconnect_notify_start;
         QueueEvent(e);
         _disconnect_notify_sent = true;
      }

      if (_disconnect_timeout && (_last_recv_time + _disconnect_timeout < now)) {
         if (!_disconnect_event_sent) {
            Log("Endpoint has stopped receiving packets for %d ms.  Disconnecting.\n", _disconnect_timeout);
            QueueEvent(Event(Event::Disconnected));
            _disconnect_event_sent = true;
         }
      }
      break;

   case Disconnected:
      if (_shutdown_timeout < now) {
         Log("Shutting down udp connection.\n");
         _udp = NULL;
         _shutdown_timeout = 0;
      }

   }
  }

  bool OnSyncRequest(INetworkReciever _, SyncRequestMessage msg) {
     Send<SyncReplyMessage>(new SyncReplyMessage {
        RandomReply = msg.RandomRequest
     });
     return true;
  }

  bool OnSyncReply(INetworkReciever _, SyncReplyMessage msg) {
     if (_current_state != State.Syncing) {
        Debug.Log("Ignoring SyncReply while not synching.");
        return false;
     }

     if (msg.RandomReply != _sync_state.Random) {
        Debug.LogFormat("sync reply {} != {}.  Keep looking...",
            msg.Random_reply, _sync_state.Random);
        return false;
     }

     if (!_connected) {
        QueueEvent(Event(Event::Connected));
        _connected = true;
     }

     Debug.LogFormat("Checking sync state ({} round trips remaining).",
         _sync_state.RoundTripsRemaining);
     if (--_sync_state.RoundTripsRemaining == 0) {
        Debug.Log("Synchronized!");
        OnSynchronized?.Invoke();
        _current_state = Running;
        _lastRecievedInput.Frame = -1;
     } else {
        int total = NUM_SYNC_PACKETS;
        int count = NUM_SYNC_PACKETS - _sync_state.RoundTripsRemaining;
        OnSynchronizing?.Invoke(total, count);
        SendSyncRequest();
     }
     return true;
  }

  bool OnInputMessage(INetworkReciever _, InputMessage msg) {
   // If a disconnect is requested, go ahead and disconnect now.
   bool disconnect_requested = msg.DisconnectRequested;
   if (disconnect_requested) {
      if (_current_state != Disconnected && !_disconnect_event_sent) {
         Log("Disconnecting endpoint on remote request.\n");
         OnDisconnected?.Invoke();
         _disconnect_event_sent = true;
      }
   } else {
      // Update the peer connection status if this peer is still considered to be
      // part of the network.
      UdpMsg::connect_status* remote_status = msg->u.input.peer_connect_status;
      for (int i = 0; i < _peerConnectStatus.Length; i++) {
         Assert.IsTrue(remote_status[i].LastFrame >= _peerConnectStatus[i].LastFrame);
         _peerConnectStatus[i].Disconnected = _peerConnectStatus[i].Disconnected || remote_status[i].Disconnected;
         _peerConnectStatus[i].LastFrame = Math.Max(_peerConnectStatus[i].LastFrame, remote_status[i].LastFrame);
      }
   }

   // Decompress the input.
   int last_received_frame_number = _last_received_input.Frame;
   if (msg.NumBits > 0) {
      int offset = 0;
      byte* bits = (uint8 *)msg.bits;
      int numBits = msg.NumBits;
      int currentFrame = msg.StartFrame;

      _last_received_input.size = msg.InputSize;
      if (_last_received_input.Frame < 0) {
         _last_received_input.Frame = msg->u.input.StartFrame - 1;
      }
      while (offset < numBits) {
         // Keep walking through the frames (parsing bits) until we reach
         // the inputs for the frame right after the one we're on.
         Assert.IsTrue(currentFrame <= (_last_received_input.Frame + 1));
         bool useInputs = currentFrame == _last_received_input.Frame + 1;

         while (BitVector.ReadBit(bits, ref offset)) {
            int bit = BitVector.ReadBit(bits, ref offset);
            int button = BitVector.ReadNibblet(bits, ref offset);
            if (useInputs) {
               if (on) {
                  _last_received_input.set(button);
               } else {
                  _last_received_input.clear(button);
               }
            }
         }
         Assert.IsTrue(offset <= numBits);

         // Now if we want to use these inputs, go ahead and send them to
         // the emulator.
         if (useInputs) {
            // Move forward 1 frame in the stream.
            Assert.IsTrue(currentFrame == _last_received_input.Frame + 1);
            _last_received_input.Frame = currentFrame;

            // Send the event to the emualtor
            OnInput?.Invoke(_lastRecievedInput);

            _state.running.last_input_packet_recv_time = timeGetTime();

            Debug.LogFormat("Sending frame {} to emu queue {} (%s).",
                _last_received_input.Frame, _queue, _lastRecievedInput);
            QueueEvent(evt);

         } else {
            Debug.Log("Skipping past frame:({}) current is {}.",
                currentFrame, _last_received_input.Frame);
         }

         // Move forward 1 frame in the input stream.
         currentFrame++;
      }
   }
   Assert.IsTrue(_last_received_input.Frame >= last_received_frame_number);

   // Get rid of our buffered input
   FlushOutputs(msg.AckFrame);
   return true;
  }

  bool OnInputAck(INetworkReciever _, InputAckMessage msg) {
     // Get rid of our buffered input
     FlushOutputs(msg.AckFrame);
     return true;
  }

  void FlushOutputs(int ackFrame) {
     while (_pending_output.Size && _pending_output.Peek().Frame < acFrame) {
        Debug.LogFormat("Throwing away pending output frame {}",
            _pending_output.Peek().Frame);
        _last_acked_input = _pending_output.Peek();
        _pending_output.Pop();
     }
  }

   void OnQualityReport(INetworkReciever _, QualityReportMessage msg) {
     // send a reply so the other side can compute the round trip transmit time.
     Send<QualityReplyMessage>(new QualityReplyMessage { Pong = msg.Ping }, Reliabilty.Unreliable);
     _remoteFrameAdvantage = msg.FrameAdvantage;
   }

  void UpdateNetworkStats() {
     uint now = GetTime();

     if (_stats_start_time == 0) {
        _stats_start_time = now;
     }

     int total_bytes_sent = _bytes_sent + (UDP_HEADER_SIZE * _packets_sent);
     float seconds = (float)((now - _stats_start_time) / 1000.0);
     float Bps = total_bytes_sent / seconds;
     float udp_overhead = (float)(100.0 * (UDP_HEADER_SIZE * _packets_sent) / _bytes_sent);

     _kbps_sent = (int)(Bps / 1024);

     Debug.LogFormat("Network Stats -- Bandwidth: %.2f KBps   Packets Sent: %5d (%.2f pps)   " +
         "KB Sent: %.2f    UDP Overhead: %.2f %%.",
         _kbps_sent,
         _packets_sent,
         (float)_packets_sent * 1000 / (now - _stats_start_time),
         total_bytes_sent / 1024.0,
         udp_overhead);
  }

}

}
