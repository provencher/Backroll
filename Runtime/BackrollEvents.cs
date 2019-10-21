namespace HouraiTeahouse.Backroll {

// The BackrollEventCode enumeration describes what type of event just happened.
//
// Backroll_EVENTCODE_CONNECTED_TO_PEER - Handshake with the game running on the
// other side of the network has been completed.
//
// Backroll_EVENTCODE_SYNCHRONIZING_WITH_PEER - Beginning the synchronization
// process with the client on the other end of the networking.  The count
// and total fields in the u.synchronizing struct of the BackrollEvent
// object indicate progress.
//
// Backroll_EVENTCODE_SYNCHRONIZED_WITH_PEER - The synchronziation with this
// peer has finished.
//
// Backroll_EVENTCODE_RUNNING - All the clients have synchronized.  You may begin
// sending inputs with ggpo_synchronize_inputs.
//
// Backroll_EVENTCODE_DISCONNECTED_FROM_PEER - The network connection on
// the other end of the network has closed.
//
// Backroll_EVENTCODE_TIMESYNC - The time synchronziation code has determined
// that this client is too far ahead of the other one and should slow
// down to ensure fairness.  The u.timesync.frames_ahead parameter in
// the BackrollEvent object indicates how many frames the client is.
public enum BackrollEventCode {
   ConnectedToPeer            = 1000,
   SynchronizingWithPeer      = 1001,
   SynchronizedWithPeer       = 1002,
   Running                    = 1003,
   DisconnectedFromPeer       = 1004,
   TimeSync                   = 1005,
   ConnectionInterrupted      = 1006,
   ConnectionResumed          = 1007,
}

// The BackrollEvent structure contains an asynchronous event notification sent
// by the on_event callback.  See BackrollEventCode, above, for a detailed
// explanation of each event.
public struct BackrollEvent {
   public BackrollEventCode     Code;
   public BackrollPlayerHandle  Player;
   public int                   SynchronizingCount;
   public int                   SynchronizingTotal;
   public int                   TimeSyncFramesAhead;
   public int                   DisconnectTimeout;
}

}
