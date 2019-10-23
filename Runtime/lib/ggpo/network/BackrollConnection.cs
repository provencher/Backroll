using HouraiTeahouse.Networking;
using System.Runtime.InteropServices;
using System;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine.Assertions;
using UnityEngine;
using Random = UnityEngine.Random;

namespace HouraiTeahouse.Backroll {

public struct BackrollConnectStatus : INetworkSerializable {
   uint data;

   public bool Disconnected {
      get => (data & 1) != 0;
      set => data = (uint)((data & ~1u) | (value ? 1u : 0u));
   }
   public int LastFrame {
      get => (int)(data << 1);
      set => data = (uint)((data & 1) | (uint)(value << 1));
   }

  public void Serialize(ref Serializer serializer) =>
     serializer.Write(data);
  public void Deserialize(ref Deserializer deserializer) =>
     data = deserializer.ReadUInt32();
}

public unsafe struct InputMessage : INetworkSerializable {

   public const int kMaxCompressedBits = 4096;

   public fixed uint                 connect_status[BackrollConstants.kMaxPlayers];
   public int                        StartFrame;

   public bool                        DisconnectRequested;
   public int                         AckFrame;

   public ushort                      NumBits;
   public uint                        InputSize; // XXX: shouldn't be in every single packet!
   public fixed byte                  bits[kMaxCompressedBits / 8]; /* must be last */

  public void Serialize(ref Serializer serializer) {
     fixed (uint* status = connect_status) {
      for (var i = 0; i < BackrollConstants.kMaxPlayers; i++) {
         // Doable since BackrollConnectionStatus is exactly the size of
         // a uint32.
         ((BackrollConnectStatus*)status)[i].Serialize(ref serializer);
      }
     }
     serializer.Write(StartFrame);
     serializer.Write(DisconnectRequested);
     serializer.Write(NumBits);
     serializer.Write(AckFrame);
     serializer.Write(InputSize);
     fixed (byte* ptr = bits) {
        serializer.Write(ptr, (ushort)Mathf.CeilToInt(NumBits / 8f));
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
     StartFrame = deserializer.ReadInt32();
     DisconnectRequested = deserializer.ReadBoolean();
     NumBits = deserializer.ReadUInt16();
     AckFrame = deserializer.ReadInt32();
     InputSize = deserializer.ReadUInt16();
     fixed (byte* ptr = bits) {
        deserializer.ReadBytes(ptr, (ushort)Mathf.CeilToInt(NumBits / 8f));
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

public unsafe class BackrollConnection : IDisposable {

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

  [StructLayout(LayoutKind.Explicit, Size=12)]
  struct ConnectionState {
    public struct SyncState {
      public uint RoundTripsRemaining;
      public uint Random;
    }
    public struct RunningState {
      public uint last_quality_report_time;
      public uint last_network_stats_interval;
      public uint LastInputPacketRecieveTime;
    }
    [FieldOffset(0)] public SyncState Sync;
    [FieldOffset(0)] public RunningState Running;
  }

  public LobbyMember LobbyMember { get; private set; }
  readonly int _queue;
  State           _current_state;
  ConnectionState _state;
  readonly MessageHandlers _messageHandlers;

   // Stats
   int            _roundTripTime;
   int            _packets_sent;
   int            _bytes_sent;
   int            _kbps_sent;
   uint           _stats_start_time;

   // Fairness.
   byte              _localFrameAdvantage;
   byte              _remoteFrameAdvantage;

   // Packet loss...
   RingBuffer<GameInput>      _pending_output;
   GameInput                  _lastRecievedInput;
   GameInput                  _lastSentInput;
   GameInput                  _last_acked_input;
   bool                       _connected;
   uint                       _lastSendTime;
   uint                       _lastReceiveTime;
   uint                       _shutdown_timeout;
   bool                       _disconnect_event_sent;
   uint                       _disconnectTimeout;
   uint                       _disconnectNotifyStart;
   bool                       _disconnect_notify_sent;

   ushort                     _next_send_seq;
   ushort                     _next_recv_seq;

   // Rift synchronization.
   TimeSync                   _timesync;

   BackrollConnectStatus[] _localConnectStatus;
   BackrollConnectStatus[] _peerConnectStatus;

   public bool IsLocal => LobbyMember.Id == LobbyMember.Lobby.UserId;

   public event Action OnConnected;
   public event Action<int /*total*/, int /*count*/> OnSynchronizing;
   public event Action OnSynchronized;
   public event Action<GameInput> OnInput;
   public event Action OnDisconnected;
   public event Action<uint /*disconnect_timeout*/> OnNetworkInterrupted;
   public event Action OnNetworkResumed;

  public BackrollConnection(LobbyMember lobbyMember, int queue, BackrollConnectStatus[] localConnectStatus) {
    if (lobbyMember == null) {
      throw new ArgumentNullException(nameof(lobbyMember));
    }
    LobbyMember = lobbyMember;
    _queue = queue;
    _localConnectStatus = localConnectStatus;

    _timesync = new TimeSync();
    _pending_output = new RingBuffer<GameInput>(64);
    _peerConnectStatus = new BackrollConnectStatus[BackrollConstants.kMaxPlayers];
    for (var i = 0; i < _peerConnectStatus.Length; i++) {
      _peerConnectStatus[i].LastFrame = -1;
    }

    _messageHandlers = new MessageHandlers();
    _messageHandlers.RegisterHandler<InputMessage>((byte)MessageCodes.Input, OnInputMessage);
    _messageHandlers.RegisterHandler<InputAckMessage>((byte)MessageCodes.InputAck, OnInputAck);
    _messageHandlers.RegisterHandler<QualityReportMessage>((byte)MessageCodes.QualityReport, OnQualityReport);
    _messageHandlers.RegisterHandler<QualityReplyMessage>((byte)MessageCodes.QualityReply, OnQualityReply);
    _messageHandlers.RegisterHandler<KeepAliveMessage>((byte)MessageCodes.KeepAlive, OnKeepAlive);
    _messageHandlers.Listen(LobbyMember);
  }

  public void Dispose() => _messageHandlers.Dispose();

   public void Synchronize() {
     if (LobbyMember != null) {
        _current_state = State.Syncing;
        _state.Sync.RoundTripsRemaining = NUM_SYNC_PACKETS;
        SendSyncRequest();
     }
  }

  public bool GetPeerConnectStatus(int id, ref int frame) {
    frame = (int)_peerConnectStatus[id].LastFrame;
    return !_peerConnectStatus[id].Disconnected;
  }

   public bool IsSynchronized => _current_state == State.Running;
   public bool IsRunning => _current_state == State.Running;

   public void SendSyncRequest() {
     unchecked {
       _state.Sync.Random = (uint)Random.Range(Int32.MinValue, Int32.MaxValue);
       Send(new SyncRequestMessage { RandomRequest = _state.Sync.Random });
     }
   }

   public void SendInput(in GameInput input) {
      if (_current_state == State.Running) {
         // Check to see if this is a good time to adjust for the rift...
         _timesync.AdvanceFrame(input, _localFrameAdvantage, _remoteFrameAdvantage);

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
     _lastSendTime = BackrollTime.GetTime();
     _bytes_sent += serializer.Position;

     LobbyMember.SendMessage(serializer.ToArray(), serializer.Position,
                              reliabilty);
   }

   public void Disconnect() {
     _current_state = State.Disconnected;
   }

  public BackrollNetworkStats GetNetworkStats() {
     return new BackrollNetworkStats {
        Ping = _roundTripTime,
        SendQueueLength = _pending_output.Size,
        KbpsSent = _kbps_sent,
        RemoteFramesBehind = _remoteFrameAdvantage,
        LocalFramesBehind = _localFrameAdvantage,
     };
  }

  void OnQualityReply(ref QualityReplyMessage msg) {
     _roundTripTime = (int)BackrollTime.GetTime() - (int)msg.Pong;
  }

  void OnKeepAlive(ref KeepAliveMessage msg) {
  }

  public void SetLocalFrameNumber(int localFrame) {
     // Estimate which frame the other guy is one by looking at the
     // last frame they gave us plus some delta for the one-way packet
     // trip time.
     int remoteFrame = _lastRecievedInput.Frame + (_roundTripTime * 60 / 1000);

     // Our frame advantage is how many frames *behind* the other guy
     // we are.  Counter-intuative, I know.  It's an advantage because
     // it means they'll have to predict more often and our moves will
     // Pop more frequenetly.
     _localFrameAdvantage = (byte)(remoteFrame - localFrame);
   }

   public int RecommendFrameDelay() =>
     _timesync.RecommendFrameWaitDuration(false);

   public void SetDisconnectTimeout(uint timeout) => _disconnectTimeout = timeout;
   public void SetDisconnectNotifyStart(uint timeout) => _disconnectNotifyStart = timeout;

   protected void SendPendingOutput() {
     Assert.IsTrue((GameInput.kMaxBytes * BackrollConstants.kMaxPlayers * 8) <
                   (1 << BitVector.kNibbleSize));
     int offset = 0;
     GameInput last;
     var msg = new InputMessage();
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
               if (UnsafeUtility.MemCmp(currentPtr, last.bits, current.Size) == 0) continue;
           }
           for (var i = 0; i < current.Size * 8; i++) {
              Assert.IsTrue(i < (1 << BitVector.kNibbleSize));
              if (current[i] == last[i]) continue;
              BitVector.SetBit(msg.bits, ref offset);
              if (current[i]) {
                 BitVector.SetBit(msg.bits, ref offset);
              } else {
                 BitVector.ClearBit(msg.bits, ref offset);
              }
              BitVector.WriteNibblet(msg.bits, i, ref offset);
           }
           BitVector.ClearBit(msg.bits, ref offset);
           last = _lastSentInput = current;
         }
      }
      msg.AckFrame = _lastRecievedInput.Frame;
      msg.NumBits = (ushort)offset;

      msg.DisconnectRequested = _current_state == State.Disconnected;
      var size = UnsafeUtility.SizeOf<BackrollConnectStatus>() * BackrollConstants.kMaxPlayers;
      if (_localConnectStatus != null) {
         fixed (BackrollConnectStatus* ptr = _localConnectStatus) {
            UnsafeUtility.MemCpy(msg.connect_status, ptr, size);
         }
      } else {
         UnsafeUtility.MemClear(msg.connect_status, size);
      }

     Assert.IsTrue(offset < InputMessage.kMaxCompressedBits);
     Send(msg);
   }

   public void Update() {
    if (LobbyMember == null) return;
    uint now = BackrollTime.GetTime();

   switch (_current_state) {
   case State.Syncing:
      int next_interval = (_state.Sync.RoundTripsRemaining == NUM_SYNC_PACKETS) ? SYNC_FIRST_RETRY_INTERVAL : SYNC_RETRY_INTERVAL;
      if (_lastSendTime != 0 && _lastSendTime + next_interval < now) {
         Debug.LogFormat("No luck syncing after {} ms... Resending sync packet.", 
            next_interval);
         SendSyncRequest();
      }
      break;

   case State.Running:
      // xxx: rig all this up with a timer wrapper
      if (_state.Running.LastInputPacketRecieveTime == 0|| 
          _state.Running.LastInputPacketRecieveTime + RUNNING_RETRY_INTERVAL < now) {
         Debug.LogFormat("Haven't exchanged packets in a while (last received:{}  last sent:{}).  Resending.", 
            _lastRecievedInput.Frame, _lastSentInput.Frame);
         SendPendingOutput();
         _state.Running.LastInputPacketRecieveTime = now;
      }

      if (_state.Running.last_quality_report_time == 0 || 
          _state.Running.last_quality_report_time + QUALITY_REPORT_INTERVAL < now) {
         Send(new QualityReportMessage {
            Ping = BackrollTime.GetTime(),
            FrameAdvantage = _localFrameAdvantage,
         });
         _state.Running.last_quality_report_time = now;
      }

      if (_state.Running.last_network_stats_interval == 0 || 
          _state.Running.last_network_stats_interval + NETWORK_STATS_INTERVAL < now) {
         UpdateNetworkStats();
         _state.Running.last_network_stats_interval =  now;
      }

      if (_lastSendTime == 0 && 
          _lastSendTime + KEEP_ALIVE_INTERVAL < now) {
         Debug.Log("Sending keep alive packet");
         Send(new KeepAliveMessage());
      }

      if (_disconnectTimeout != 0 && _disconnectNotifyStart != 0 &&
         !_disconnect_notify_sent && (_lastReceiveTime + _disconnectNotifyStart < now)) {
         Debug.LogFormat("Endpoint has stopped receiving packets for {} ms.  Sending notification.", 
            _disconnectNotifyStart);
         uint disconnectTimeout = _disconnectTimeout - _disconnectNotifyStart;
         OnNetworkInterrupted?.Invoke(disconnectTimeout);
         _disconnect_notify_sent = true;
      }

      if (_disconnectTimeout != 0 && (_lastReceiveTime + _disconnectTimeout < now)) {
         if (!_disconnect_event_sent) {
            Debug.LogFormat("Endpoint has stopped receiving packets for {} ms.  Disconnecting.", 
               _disconnectTimeout);
            OnDisconnected?.Invoke();
            _disconnect_event_sent = true;
         }
      }
      break;

   case State.Disconnected:
      if (_shutdown_timeout < now) {
         Debug.Log("Shutting down udp connection.");
         LobbyMember = null;
         _shutdown_timeout = 0;
      }
      break;
   }
  }

  void OnSyncRequest(ref SyncRequestMessage msg) {
     Send<SyncReplyMessage>(new SyncReplyMessage {
        RandomReply = msg.RandomRequest
     });
  }

  void OnSyncReply(ref SyncReplyMessage msg) {
     if (_current_state != State.Syncing) {
        Debug.Log("Ignoring SyncReply while not synching.");
        return;
     }

     if (msg.RandomReply != _state.Sync.Random) {
        Debug.LogFormat("sync reply {} != {}.  Keep looking...",
            msg.RandomReply, _state.Sync.Random);
        return;
     }

     if (!_connected) {
        OnConnected?.Invoke();
        _connected = true;
     }

     Debug.LogFormat("Checking sync state ({} round trips remaining).",
         _state.Sync.RoundTripsRemaining);
     if (--_state.Sync.RoundTripsRemaining == 0) {
        Debug.Log("Synchronized!");
        OnSynchronized?.Invoke();
        _current_state = State.Running;
        _lastRecievedInput.Frame = -1;
     } else {
        int total = NUM_SYNC_PACKETS;
        int count = NUM_SYNC_PACKETS - (int)_state.Sync.RoundTripsRemaining;
        OnSynchronizing?.Invoke(total, count);
        SendSyncRequest();
     }
  }

  void OnInputMessage(ref InputMessage msg) {
   // If a disconnect is requested, go ahead and disconnect now.
   bool disconnect_requested = msg.DisconnectRequested;
   if (disconnect_requested) {
      if (_current_state != State.Disconnected && !_disconnect_event_sent) {
         Debug.Log("Disconnecting endpoint on remote request.");
         OnDisconnected?.Invoke();
         _disconnect_event_sent = true;
      }
   } else {
      // Update the peer connection status if this peer is still considered to be
      // part of the network.
      fixed (uint* ptr = msg.connect_status) {
         var remote_status = (BackrollConnectStatus*)ptr;
         for (int i = 0; i < _peerConnectStatus.Length; i++) {
            Assert.IsTrue(remote_status[i].LastFrame >= _peerConnectStatus[i].LastFrame);
            _peerConnectStatus[i].Disconnected = _peerConnectStatus[i].Disconnected || remote_status[i].Disconnected;
            _peerConnectStatus[i].LastFrame = Math.Max(_peerConnectStatus[i].LastFrame, remote_status[i].LastFrame);
         }
      }
   }

   // Decompress the input.
   int last_received_frame_number = _lastRecievedInput.Frame;
   if (msg.NumBits > 0) {
      int offset = 0;
      int numBits = msg.NumBits;
      int currentFrame = msg.StartFrame;

      _lastRecievedInput.Size = msg.InputSize;
      if (_lastRecievedInput.Frame < 0) {
         _lastRecievedInput.Frame = msg.StartFrame - 1;
      }
      fixed (byte* ptr = msg.bits) {
         while (offset < numBits) {
            // Keep walking through the frames (parsing bits) until we reach
            // the inputs for the frame right after the one we're on.
            Assert.IsTrue(currentFrame <= (_lastRecievedInput.Frame + 1));
            bool useInputs = currentFrame == _lastRecievedInput.Frame + 1;

            while (BitVector.ReadBit(ptr, ref offset)) {
               bool bit = BitVector.ReadBit(ptr, ref offset);
               int button = BitVector.ReadNibblet(ptr, ref offset);
               if (useInputs) {
                  if (bit) {
                     _lastRecievedInput.Set(button);
                  } else {
                     _lastRecievedInput.Clear(button);
                  }
               }
            }
            Assert.IsTrue(offset <= numBits);

            // Now if we want to use these inputs, go ahead and send them to
            // the emulator.
            if (useInputs) {
               // Move forward 1 frame in the stream.
               Assert.IsTrue(currentFrame == _lastRecievedInput.Frame + 1);
               _lastRecievedInput.Frame = currentFrame;

               // Send the event to the emualtor
               OnInput?.Invoke(_lastRecievedInput);

               _state.Running.LastInputPacketRecieveTime = BackrollTime.GetTime();

               Debug.LogFormat("Sending frame {} to emu queue {} ({}).",
                  _lastRecievedInput.Frame, _queue, _lastRecievedInput);
            } else {
               Debug.LogFormat("Skipping past frame:({}) current is {}.",
                  currentFrame, _lastRecievedInput.Frame);
            }

            // Move forward 1 frame in the input stream.
            currentFrame++;
         }
      }
   }
   Assert.IsTrue(_lastRecievedInput.Frame >= last_received_frame_number);

   // Get rid of our buffered input
   FlushOutputs(msg.AckFrame);
  }

  void OnInputAck(ref InputAckMessage msg) {
     // Get rid of our buffered input
     FlushOutputs(msg.AckFrame);
  }

  void FlushOutputs(int ackFrame) {
     while (!_pending_output.IsEmpty && _pending_output.Peek().Frame < ackFrame) {
        Debug.LogFormat("Throwing away pending output frame {}",
            _pending_output.Peek().Frame);
        _last_acked_input = _pending_output.Peek();
        _pending_output.Pop();
     }
  }

   void OnQualityReport(ref QualityReportMessage msg) {
     // send a reply so the other side can compute the round trip transmit time.
     Send<QualityReplyMessage>(new QualityReplyMessage { Pong = msg.Ping }, Reliabilty.Unreliable);
     _remoteFrameAdvantage = msg.FrameAdvantage;
   }

  void UpdateNetworkStats() {
     uint now = BackrollTime.GetTime();

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
