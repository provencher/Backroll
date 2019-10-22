/* -----------------------------------------------------------------------
 * GGPO.net (http://ggpo.net)  -  Copyright 2009 GroundStorm Studios, LLC.
 *
 * Use of this software is governed by the MIT license that can be found
 * in the LICENSE file.
 */

#include "types.h"
#include "udp_proto.h"
#include "bitvector.h"

UdpProtocol::UdpProtocol() :
   _local_frame_advantage(0),
   _remote_frame_advantage(0),
   _queue(-1),
   _magic_number(0),
   _remote_magic_number(0),
   _packets_sent(0),
   _bytes_sent(0),
   _stats_start_time(0),
   _last_send_time(0),
   _shutdown_timeout(0),
   _disconnect_timeout(0),
   _disconnect_notify_start(0),
   _disconnect_notify_sent(false),
   _disconnect_event_sent(false),
   _connected(false),
   _next_send_seq(0),
   _next_recv_seq(0),
   _udp(NULL)
{
   _last_sent_input.init(-1, NULL, 1);
   _last_received_input.init(-1, NULL, 1);
   _last_acked_input.init(-1, NULL, 1);

   memset(&_state, 0, sizeof _state);
   memset(_peer_connect_status, 0, sizeof(_peer_connect_status));
   for (int i = 0; i < ARRAY_SIZE(_peer_connect_status); i++) {
      _peer_connect_status[i].last_frame = -1;
   }
   memset(&_peer_addr, 0, sizeof _peer_addr);
   _oo_packet.msg = NULL;

   char *delay = getenv("ggpo.network.delay");
   char *oop_pct = getenv("ggpo.oop.percent");
   _send_latency = delay ? atoi(delay) : 0;
   _oop_percent = oop_pct ? atoi(oop_pct) : 0;
}

UdpProtocol::~UdpProtocol()
{
   ClearSendQueue();
}

void
UdpProtocol::Init(Udp *udp,
                  Poll &poll,
                  int queue,
                  char *ip,
                  int port,
                  UdpMsg::connect_status *status)
{
   _udp = udp;
   _queue = queue;
   _local_connect_status = status;

   _peer_addr.sin_family = AF_INET;
   _peer_addr.sin_addr.s_addr = inet_addr(ip);
   _peer_addr.sin_port = htons(port);

   do {
      _magic_number = rand();
   } while (_magic_number == 0);
   poll.RegisterLoop(this);
}

bool
UdpProtocol::GetEvent(UdpProtocol::Event &e)
{
   if (_event_queue.size() == 0) {
      return false;
   }
   e = _event_queue.front();
   _event_queue.pop();
   return true;
}


bool
UdpProtocol::OnLoopPoll(void *cookie)
{
   if (!_udp) {
      return true;
   }

   unsigned int now = timeGetTime();
   unsigned int next_interval;

   PumpSendQueue();
   switch (_current_state) {
   case Syncing:
      next_interval = (_state.sync.roundtrips_remaining == NUM_SYNC_PACKETS) ? SYNC_FIRST_RETRY_INTERVAL : SYNC_RETRY_INTERVAL;
      if (_last_send_time && _last_send_time + next_interval < now) {
         Log("No luck syncing after %d ms... Re-queueing sync packet.\n", next_interval);
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


   return true;
}

void
UdpProtocol::SendSyncRequest()
{
   _state.sync.random = rand() & 0xFFFF;
   UdpMsg *msg = new UdpMsg(UdpMsg::SyncRequest);
   msg->u.sync_request.random_request = _state.sync.random;
   SendMsg(msg);
}

void
UdpProtocol::SendMsg(UdpMsg *msg)
{
   LogMsg("send", msg);

   _packets_sent++;
   _last_send_time = timeGetTime();
   _bytes_sent += msg->PacketSize();

   msg->hdr.magic = _magic_number;
   msg->hdr.sequence_number = _next_send_seq++;

   _send_queue.push(QueueEntry(timeGetTime(), _peer_addr, msg));
   PumpSendQueue();
}

bool
UdpProtocol::HandlesMsg(sockaddr_in &from,
                        UdpMsg *msg)
{
   if (!_udp) {
      return false;
   }
   return _peer_addr.sin_addr.S_un.S_addr == from.sin_addr.S_un.S_addr &&
          _peer_addr.sin_port == from.sin_port;
}

void
UdpProtocol::OnMsg(UdpMsg *msg, int len)
{
   bool handled = false;
   typedef bool (UdpProtocol::*DispatchFn)(UdpMsg *msg, int len);
   static const DispatchFn table[] = {
      &UdpProtocol::OnInvalid,             /* Invalid */
      &UdpProtocol::OnSyncRequest,         /* SyncRequest */
      &UdpProtocol::OnSyncReply,           /* SyncReply */
      &UdpProtocol::OnInput,               /* Input */
      &UdpProtocol::OnQualityReport,       /* QualityReport */
      &UdpProtocol::OnQualityReply,        /* QualityReply */
      &UdpProtocol::OnKeepAlive,           /* KeepAlive */
      &UdpProtocol::OnInputAck,            /* InputAck */
   };

   // filter out messages that don't match what we expect
   int seq = msg->hdr.sequence_number;
   if (msg->hdr.type != UdpMsg::SyncRequest &&
       msg->hdr.type != UdpMsg::SyncReply) {
      if (msg->hdr.magic != _remote_magic_number) {
         LogMsg("recv rejecting", msg);
         return;
      }

      // filter out out-of-order packets
      uint16 skipped = seq - _next_recv_seq;
      // Log("checking sequence number -> next - seq : %d - %d = %d\n", seq, _next_recv_seq, skipped);
      if (skipped > MAX_SEQ_DISTANCE) {
         Log("dropping out of order packet (seq: %d, last seq:%d)\n", seq, _next_recv_seq);
         return;
      }
   }

   _next_recv_seq = seq;
   LogMsg("recv", msg);
   if (msg->hdr.type >= ARRAYSIZE(table)) {
      OnInvalid(msg, len);
   } else {
      handled = (this->*(table[msg->hdr.type]))(msg, len);
   }
   if (handled) {
      _last_recv_time = timeGetTime();
      if (_disconnect_notify_sent && _current_state == Running) {
         QueueEvent(Event(Event::NetworkResumed));
         _disconnect_notify_sent = false;
      }
   }
}



void
UdpProtocol::QueueEvent(const UdpProtocol::Event &evt)
{
   LogEvent("Queuing event", evt);
   _event_queue.push(evt);
}

bool
UdpProtocol::GetPeerConnectStatus(int id, int *frame)
{
   *frame = _peer_connect_status[id].last_frame;
   return !_peer_connect_status[id].disconnected;
}

bool
UdpProtocol::OnInvalid(UdpMsg *msg, int len)
{
   Assert.IsTrue(FALSE && "Invalid msg in UdpProtocol");
   return false;
}

bool
UdpProtocol::OnSyncRequest(UdpMsg *msg, int len)
{
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


bool
UdpProtocol::OnInput(UdpMsg *msg, int len)
{
   /*
    * If a disconnect is requested, go ahead and disconnect now.
    */
   bool disconnect_requested = msg->u.input.disconnect_requested;
   if (disconnect_requested) {
      if (_current_state != Disconnected && !_disconnect_event_sent) {
         Log("Disconnecting endpoint on remote request.\n");
         QueueEvent(Event(Event::Disconnected));
         _disconnect_event_sent = true;
      }
   } else {
      /*
       * Update the peer connection status if this peer is still considered to be part
       * of the network.
       */
      UdpMsg::connect_status* remote_status = msg->u.input.peer_connect_status;
      for (int i = 0; i < ARRAYSIZE(_peer_connect_status); i++) {
         Assert.IsTrue(remote_status[i].last_frame >= _peer_connect_status[i].last_frame);
         _peer_connect_status[i].disconnected = _peer_connect_status[i].disconnected || remote_status[i].disconnected;
         _peer_connect_status[i].last_frame = MAX(_peer_connect_status[i].last_frame, remote_status[i].last_frame);
      }
   }

   /*
    * Decompress the input.
    */
   int last_received_frame_number = _last_received_input.frame;
   if (msg->u.input.num_bits) {
      int offset = 0;
      uint8 *bits = (uint8 *)msg->u.input.bits;
      int numBits = msg->u.input.num_bits;
      int currentFrame = msg->u.input.start_frame;

      _last_received_input.size = msg->u.input.input_size;
      if (_last_received_input.frame < 0) {
         _last_received_input.frame = msg->u.input.start_frame - 1;
      }
      while (offset < numBits) {
         /*
          * Keep walking through the frames (parsing bits) until we reach
          * the inputs for the frame right after the one we're on.
          */
         Assert.IsTrue(currentFrame <= (_last_received_input.frame + 1));
         bool useInputs = currentFrame == _last_received_input.frame + 1;

         while (BitVector_ReadBit(bits, &offset)) {
            int on = BitVector_ReadBit(bits, &offset);
            int button = BitVector_ReadNibblet(bits, &offset);
            if (useInputs) {
               if (on) {
                  _last_received_input.set(button);
               } else {
                  _last_received_input.clear(button);
               }
            }
         }
         Assert.IsTrue(offset <= numBits);

         /*
          * Now if we want to use these inputs, go ahead and send them to
          * the emulator.
          */
         if (useInputs) {
            /*
             * Move forward 1 frame in the stream.
             */
            char desc[1024];
            Assert.IsTrue(currentFrame == _last_received_input.frame + 1);
            _last_received_input.frame = currentFrame;

            /*
             * Send the event to the emualtor
             */
            UdpProtocol::Event evt(UdpProtocol::Event::Input);
            evt.u.input.input = _last_received_input;

            _last_received_input.desc(desc);

            _state.running.last_input_packet_recv_time = timeGetTime();

            Log("Sending frame %d to emu queue %d (%s).\n", _last_received_input.frame, _queue, desc);
            QueueEvent(evt);

         } else {
            Log("Skipping past frame:(%d) current is %d.\n", currentFrame, _last_received_input.frame);
         }

         /*
          * Move forward 1 frame in the input stream.
          */
         currentFrame++;
      }
   }
   Assert.IsTrue(_last_received_input.frame >= last_received_frame_number);

   /*
    * Get rid of our buffered input
    */
   while (_pending_output.size() && _pending_output.front().frame < msg->u.input.ack_frame) {
      Log("Throwing away pending output frame %d\n", _pending_output.front().frame);
      _last_acked_input = _pending_output.front();
      _pending_output.pop();
   }
   return true;
}




void
UdpProtocol::PumpSendQueue()
{
   while (!_send_queue.empty()) {
      QueueEntry &entry = _send_queue.front();

      if (_send_latency) {
         // should really come up with a gaussian distributation based on the configured
         // value, but this will do for now.
         int jitter = (_send_latency * 2 / 3) + ((rand() % _send_latency) / 3);
         if (timeGetTime() < _send_queue.front().queue_time + jitter) {
            break;
         }
      }
      if (_oop_percent && !_oo_packet.msg && ((rand() % 100) < _oop_percent)) {
         int delay = rand() % (_send_latency * 10 + 1000);
         Log("creating rogue oop (seq: %d  delay: %d)\n", entry.msg->hdr.sequence_number, delay);
         _oo_packet.send_time = timeGetTime() + delay;
         _oo_packet.msg = entry.msg;
         _oo_packet.dest_addr = entry.dest_addr;
      } else {
         Assert.IsTrue(entry.dest_addr.sin_addr.s_addr);

         _udp->SendTo((char *)entry.msg, entry.msg->PacketSize(), 0,
                      (struct sockaddr *)&entry.dest_addr, sizeof entry.dest_addr);

         delete entry.msg;
      }
      _send_queue.pop();
   }
   if (_oo_packet.msg && _oo_packet.send_time < timeGetTime()) {
      Log("sending rogue oop!");
      _udp->SendTo((char *)_oo_packet.msg, _oo_packet.msg->PacketSize(), 0,
                     (struct sockaddr *)&_oo_packet.dest_addr, sizeof _oo_packet.dest_addr);

      delete _oo_packet.msg;
      _oo_packet.msg = NULL;
   }
}

void
UdpProtocol::ClearSendQueue()
{
   while (!_send_queue.empty()) {
      delete _send_queue.front().msg;
      _send_queue.pop();
   }
}
