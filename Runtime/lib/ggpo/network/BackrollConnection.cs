using HouraiTeahouse.Networking;

namespace HouraiTeahouse.Backroll {

public struct InputMessage : INetworkSerializable {

  readonly RingBuffer<GameInput> _input;

  public InputMessage(RingBuffer<GameInput> input) {
    _input = input;
  }

}

public struct InputAckMessage : INetworkSerializable {
  public int AckFrame;

  public void Serialize(ref Serializer serializer) =>
    serializer.Write(AckFrame);
  public void Deseriialize(ref Deserializer deserializer) =>
    deserializer.ReadInt32(ref AckFrame);
}

public struct QualityReportMessage : INetworkSerializable {
  public byte FrameAdvantage;
  public uint Ping;

  public void Serialize(ref Serializer serializer)  {
    serializer.Write(FrameAdvantage);
    serializer.Write(Ping);
  }

  public void Deseriialize(ref Deserializer deserializer) {
    deserializer.ReadByte(ref FrameAdvantage);
    deserializer.ReadInt32(ref Ping);
  }
}

public struct QualityReplyMessage : INetworkSerializable {
  public uint Pong;

  public void Serialize(ref Serializer serializer) =>
    serializer.Write(Pong);
  public void Deseriialize(ref Deserializer deserializer) =>
    deserializer.ReadInt32(ref Pong);
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
    Input             = 1;
    InputAck          = 2;
    QualityReport     = 3;
    QualityReply      = 4;
    KeepAlive         = 5;
  }

  LobbyMember _lobbyMember;
  State          _current_state;
  readonly MessageHandlers _messageHandlers

   // Stats
   int            _roundTripTime;
   int            _packets_sent;
   int            _bytes_sent;
   int            _kbps_sent;
   int            _stats_start_time;

   // Fairness.
   int               _local_frame_advantage;
   int               _remote_frame_advantage;

   // Packet loss...
   RingBuffer<GameInput, 64>  _pending_output;
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

  public BackrollConnection(LobbyMember lobbyMember) {
    _lobbyMember = lobbyMember;

    _messageHandlers = new MessageHandlers();
    _messageHandlers.Register((byte)MessageCodes.Input, OnInputMsg);
    _messageHandlers.Register((byte)MessageCodes.InputAck, OnInputMsg);
    _messageHandlers.Register((byte)MessageCodes.QualityReport, OnQualityReport);
    _messageHandlers.Register((byte)MessageCodes.QualityReply, OnQualityReply);
    _messageHandlers.Register((byte)MessageCodes.KeepAlive, OnKeepAlive);
    _messageHandlers.Listen(_lobbyMember);
  }

  public void Dispose() => _messageHandlers.Dispose();

   public void Synchronize() {
     if (_lobbyMember != null) {
        _current_state = Syncing;
        _state.sync.roundtrips_remaining = NUM_SYNC_PACKETS;
        SendSyncRequest();
     }
  }

   bool GetPeerConnectStatus(int id, int *frame);

   public bool IsInitialized  => _lobbyMember != null;
   public bool IsSynchronized => _current_state == Running;
   public bool IsRunning => _current_state == Running;

   public void SendInput(GameInput &input) {
      if (_current_state == Running) {
         /*
          * Check to see if this is a good time to adjust for the rift...
          */
         _timesync.advance_frame(input, _local_frame_advantage, _remote_frame_advantage);

         /*
          * Save this input packet
          *
          * XXX: This queue may fill up for spectators who do not ack input packets in a timely
          * manner.  When this happens, we can either resize the queue (ug) or disconnect them
          * (better, but still ug).  For the meantime, make this queue really big to decrease
          * the odds of this happening...
          */
         _pending_output.push(input);
      }
      SendPendingOutput();
   }

   public void SendInputAck() {
     Send(new InputAckMessage { AckFrame = _lastRecievedInput.Frame });
   }

   void unsafe Send<T>(in T msg, Reliability reliability = Reliability.Reliable) {
    var buffer = stackalloc byte[2048];
    var serializer = Serializer.Create(buffer, (uint)SerializationConstants.kMaxMessageSize);
    Serialize<T>(msg, ref serializer);

    _packets_sent++;
    _last_send_time = GetTime();
    _bytes_sent += serializer.Position;

    _lobbyMember.SendMessage(serializer.ToArray(), serializer.Position,
                             reliability);
   }

   uint GetTime() => (uint)Mathf.FloorToInt(Time.realtimeSinceStartup * 1000);

   public void Disconnect() {
     _current_state = Disconnected;
   }

  BackrollNetworkStats GetNetworkStats(struct GGPONetworkStats *s) {
     BackrollNetworkStats stats;
     stats.network.ping = _roundTripTime;
     stats.network.send_queue_len = _pending_output.size();
     stats.network.kbps_sent = _kbps_sent;
     stats.timesync.remote_frames_behind = _remote_frame_advantage;
     stats.timesync.local_frames_behind = _local_frame_advantage;
     return stats;
  }

  bool OnQualityReply(UdpMsg *msg, int len) {
     _roundTripTime = timeGetTime() - msg->u.quality_reply.pong;
     return true;
  }

  bool OnKeepAlive(UdpMsg *msg, int len) {
     return true;
  }

   void SetLocalFrameNumber(int num) {
     // Estimate which frame the other guy is one by looking at the
     // last frame they gave us plus some delta for the one-way packet
     // trip time.
     int remoteFrame = _lastRecievedInput.frame + (_roundTripTime * 60 / 1000);

     // Our frame advantage is how many frames *behind* the other guy
     // we are.  Counter-intuative, I know.  It's an advantage because
     // it means they'll have to predict more often and our moves will
     // pop more frequenetly.
     _local_frame_advantage = remoteFrame - localFrame;
   }

   int RecommendFrameDelay() =>
     _timesync.recommend_frame_wait_duration(false);

   void SetDisconnectTimeout(int timeout); =>
     _disconnect_timeout = timeout;
   void SetDisconnectNotifyStart(int timeout) =>
     _disconnect_notify_start = timeout;

   protected void SendPendingOutput() {
     Assert.IsTrue((GAMEINPUT_MAX_BYTES * GAMEINPUT_MAX_PLAYERS * 8) <
                   (1 << BITVECTOR_NIBBLE_SIZE));
     UdpMsg *msg = new UdpMsg(UdpMsg::Input);
     int i, j, offset = 0;
     uint8 *bits;
     GameInput last;

     if (_pending_output.size()) {
        last = _last_acked_input;
        bits = msg->u.input.bits;

        msg->u.input.start_frame = _pending_output.front().frame;
        msg->u.input.input_size = _pending_output.front().size;

        Assert.IsTrue(last.frame == -1 ||
                      last.frame + 1 == msg->u.input.start_frame);
        for (j = 0; j < _pending_output.size(); j++) {
           ref GameInput current = ref _pending_output.item(j);
           if (memcmp(current.bits, last.bits, current.size) != 0) {
              for (i = 0; i < current.size * 8; i++) {
                 Assert.IsTrue(i < (1 << BITVECTOR_NIBBLE_SIZE));
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
     msg->u.input.ack_frame = _lastRecievedInput.frame;
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

  bool OnSyncRequest(UdpMsg *msg, int len) {
     if (_remote_magic_number != 0 && msg->hdr.magic != _remote_magic_number) {
        Log("Ignoring sync request from unknown endpoint (%d != %d).\n",
             msg->hdr.magic, _remote_magic_number);
        return false;
     }
     UdpMsg *reply = new UdpMsg(UdpMsg::SyncReply);
     reply->u.sync_reply.random_reply = msg->u.sync_request.random_request;
     SendMsg(reply);
     return true;
  }

  bool OnSyncReply(UdpMsg *msg, int len) {
     if (_current_state != Syncing) {
        Log("Ignoring SyncReply while not synching.\n");
        return msg->hdr.magic == _remote_magic_number;
     }

     if (msg->u.sync_reply.random_reply != _state.sync.random) {
        Log("sync reply %d != %d.  Keep looking...\n",
            msg->u.sync_reply.random_reply, _state.sync.random);
        return false;
     }

     if (!_connected) {
        QueueEvent(Event(Event::Connected));
        _connected = true;
     }

     Log("Checking sync state (%d round trips remaining).\n", _state.sync.roundtrips_remaining);
     if (--_state.sync.roundtrips_remaining == 0) {
        Log("Synchronized!\n");
        QueueEvent(UdpProtocol::Event(UdpProtocol::Event::Synchronzied));
        _current_state = Running;
        _lastRecievedInput.frame = -1;
        _remote_magic_number = msg->hdr.magic;
     } else {
        UdpProtocol::Event evt(UdpProtocol::Event::Synchronizing);
        evt.u.synchronizing.total = NUM_SYNC_PACKETS;
        evt.u.synchronizing.count = NUM_SYNC_PACKETS - _state.sync.roundtrips_remaining;
        QueueEvent(evt);
        SendSyncRequest();
     }
     return true;
  }

  bool OnInputAck(INetworkReciever _, QualityReportMessage msg) {
     // Get rid of our buffered input
     while (_pending_output.size() &&
         _pending_output.front().frame < msg.AckFrame) {
        Log("Throwing away pending output frame %d\n", _pending_output.front().frame);
        _last_acked_input = _pending_output.front();
        _pending_output.pop();
     }
     return true;
  }

   bool OnQualityReport(INetworkReciever _, QualityReportMessage msg) {
     // send a reply so the other side can compute the round trip transmit time.
     _messageHandlers.Send(_lobbyMember,
         new QualityReplyMessage { Pong = msg.Ping });
     _remote_frame_advantage = msg.Frame_advantage;
   }

  void UpdateNetworkStats() {
     int now = timeGetTime();

     if (_stats_start_time == 0) {
        _stats_start_time = now;
     }

     int total_bytes_sent = _bytes_sent + (UDP_HEADER_SIZE * _packets_sent);
     float seconds = (float)((now - _stats_start_time) / 1000.0);
     float Bps = total_bytes_sent / seconds;
     float udp_overhead = (float)(100.0 * (UDP_HEADER_SIZE * _packets_sent) / _bytes_sent);

     _kbps_sent = int(Bps / 1024);

     Log("Network Stats -- Bandwidth: %.2f KBps   Packets Sent: %5d (%.2f pps)   "
         "KB Sent: %.2f    UDP Overhead: %.2f %%.\n",
         _kbps_sent,
         _packets_sent,
         (float)_packets_sent * 1000 / (now - _stats_start_time),
         total_bytes_sent / 1024.0,
         udp_overhead);
  }

}

}
