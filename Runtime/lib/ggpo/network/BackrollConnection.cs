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
   public uint                        start_frame;

   public int                         disconnect_requested;
   public int                         ack_frame;

   public ushort                      num_bits;
   public uint                        input_size; // XXX: shouldn't be in every single packet!
   public fixed byte                  bits[4096]; /* must be last */

//   readonly RingBuffer<GameInput> _input;

//   public InputMessage(RingBuffer<GameInput> input) {
//     _input = input;
//   }

  public void Serialize(ref Serializer serializer) {
     fixed (uint* status = connect_status) {
      for (var i = 0; i < BackrollConstants.kMaxPlayers; i++) {
         // Doable since BackrollConnectionStatus is exactly the size of
         // a uint32.
         ((BackrollConnectStatus*)status)[i].Serialize(ref serializer);
      }
     }
     serializer.Write(start_frame);
     serializer.Write(disconnect_requested);
     serializer.Write(ack_frame);
     serializer.Write(input_size);
     fixed (byte* ptr = bits) {
        serializer.Write(ptr, input_size);
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
     start_frame = deserializer.ReadUInt32();
     disconnect_requested = deserializer.ReadInt32();
     ack_frame = deserializer.ReadInt32();
     input_size = deserializer.ReadUInt16();
     fixed (byte* ptr = bits) {
        deserializer.ReadBytes(ptr, input_size);
     }
  }
}

public struct SyncRequestMessage : INetworkSerializable {
  public void Serialize(ref Serializer serializer) {}
  public void Deserialize(ref Deserializer deserializer) {}
}

public struct SyncReplyMessage : INetworkSerializable {
  public void Serialize(ref Serializer serializer) {}
  public void Deserialize(ref Deserializer deserializer) {}
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

  public LobbyMember LobbyMember { get; }
  State          _current_state;
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

   public event Action OnConnected;
   public event Action<int /*total*/, int /*count*/> OnSynchronizing;
   public event Action OnSynchronized;
   public event Action<GameInput> OnInput;
   public event Action OnDisconnected;
   public event Action<int /*disconnect_timeout*/> OnNetworkInterrupted;
   public event Action OnNetworkResumed;

  public BackrollConnection(LobbyMember lobbyMember) {
    LobbyMember = lobbyMember;

    _pending_output = new RingBuffer<GameInput>(64);

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
        _state.sync.roundtrips_remaining = NUM_SYNC_PACKETS;
        SendSyncRequest();
     }
  }

   // bool GetPeerConnectStatus(int id, ref int frame);

   public bool IsInitialized  => LobbyMember != null;
   public bool IsSynchronized => _current_state == State.Running;
   public bool IsRunning => _current_state == State.Running;

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
     int i, j, offset = 0;
     byte* bits;
     GameInput last;

     if (_pending_output.size()) {
        last = _last_acked_input;
        bits = msg.Input.bits;

        msg->u.input.start_frame = _pending_output.Peek().Frame;
        msg->u.input.input_size = _pending_output.Peek().size;

        Assert.IsTrue(last.Frame == -1 ||
                      last.Frame + 1 == msg->u.input.start_frame);
        for (j = 0; j < _pending_output.size(); j++) {
           ref GameInput current = ref _pending_output.item(j);
           if (memcmp(current.bits, last.bits, current.size) != 0) {
              for (i = 0; i < current.size * 8; i++) {
                 Assert.IsTrue(i < (1 << BitVector.kNibbleSize));
                 if (current.value(i) != last.value(i)) {
                    BitVector.SetBit(msg->u.input.bits, &offset);
                    (current.value(i) ? BitVector_SetBit : BitVector_ClearBit)(bits, &offset);
                    BitVector.WriteNibblet(bits, i, &offset);
                 }
              }
           }
           BitVector.ClearBit(msg->u.input.bits, &offset);
           last = _lastSentInput = current;
        }
     } else {
        msg->u.input.start_frame = 0;
        msg->u.input.input_size = 0;
     }
     msg->u.input.ack_frame = _lastRecievedInput.Frame;
     msg->u.input.num_bits = offset;

     msg->u.input.disconnect_requested = _current_state == Disconnected;
     if (_local_connect_status) {
        memcpy(msg->u.input.peer_connect_status, _local_connect_status, sizeof(UdpMsg::connect_status) * UDP_MSG_MAX_PLAYERS);
     } else {
        memset(msg->u.input.peer_connect_status, 0, sizeof(UdpMsg::connect_status) * UDP_MSG_MAX_PLAYERS);
     }

     Assert.IsTrue(offset < MAX_COMPRESSED_BITS);

     SendMsg(msg);
   }

  bool OnSyncRequest(INetworkReciever _, SyncRequestMessage msg) {
     if (_remote_magic_number != 0 && msg->hdr.magic != _remote_magic_number) {
        Debug.LogFormat("Ignoring sync request from unknown endpoint ({} != {}).",
             msg->hdr.magic, _remote_magic_number);
        return false;
     }
     UdpMsg *reply = new UdpMsg(UdpMsg::SyncReply);
     reply->u.sync_reply.random_reply = msg->u.sync_request.random_request;
     SendMsg(reply);
     return true;
  }

  bool OnSyncReply(INetworkReciever _, SyncReplyMessage msg) {
     if (_current_state != Syncing) {
        Debug.Log("Ignoring SyncReply while not synching.");
        return msg.magic == _remote_magic_number;
     }

     if (msg->u.sync_reply.random_reply != _state.sync.random) {
        Debug.LogFormat("sync reply {} != {}.  Keep looking...",
            msg->u.sync_reply.random_reply, _state.sync.random);
        return false;
     }

     if (!_connected) {
        QueueEvent(Event(Event::Connected));
        _connected = true;
     }

     Debug.LogFormat("Checking sync state ({} round trips remaining).", _state.sync.roundtrips_remaining);
     if (--_state.sync.roundtrips_remaining == 0) {
        Debug.Log("Synchronized!");
        QueueEvent(new Event(EventType.Synchronzied));
        _current_state = Running;
        _lastRecievedInput.Frame = -1;
        _remote_magic_number = msg->hdr.magic;
     } else {
        var evt = new Event(EventType.Synchronizing);
        evt.u.synchronizing.total = NUM_SYNC_PACKETS;
        evt.u.synchronizing.count = NUM_SYNC_PACKETS - _state.sync.roundtrips_remaining;
        QueueEvent(evt);
        SendSyncRequest();
     }
     return true;
  }

  bool OnInputAck(INetworkReciever _, InputAckMessage msg) {
     // Get rid of our buffered input
     while (_pending_output.Size && _pending_output.Peek().Frame < msg.AckFrame) {
        Debug.LogFormat("Throwing away pending output frame {}", _pending_output.Peek().Frame);
        _last_acked_input = _pending_output.Peek();
        _pending_output.Pop();
     }
     return true;
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
