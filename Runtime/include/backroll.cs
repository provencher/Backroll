using System;
using Unity.Collections.Lowlevel.Unsafe;

namespace HouraiTeahouse.Backroll {

public static class BackrollConstants {
  public const int kMaxPlayers = 8;
  public const int kMaxPredictionFrames = 8;
  public const int kMaxSpectators = 32;
  public const int kSpectatorInputInterval = 4;

  public const int kInvalidHandle = -1;
}

public struct BackrollPlayerHandle {
  public int Id;
}

public enum BackrollPlayerType {
   Local,
   Remote,
   Spectator
}

// The BackrollPlayer structure used to describe players in ggpo_add_player
//
// type: One of the BackrollPlayerType values describing how inputs should be
// handled
//      Local players must have their inputs updated every frame via
//      AddLocalInputs.  Remote players values will come over the
//      network.
//
// player_id: The player number.  Should be between 1 and the number of
// players
//       In the game (e.g. in a 2 player game, either 1 or 2).
public struct BackrollPlayer {
   BackrollPlayerType    Type;
   int                   Player_id;
   LobbyMember           LobbyMember;
}

public struct BackrollLocalEndpoint {
   int      player_num;
}

public class BackrollException : Exception {
}

public class InRollbackException : BackrollException {
}

public class InvalidRequestException : BackrollException {
}

public enum BackrollErrorCode : sbyte {
  OK                  = 0,
  Success             = 0,
  GeneralFailure      = -1,
  InvalidSession      = 1,
  InvalidPlayerHandle = 2,
  PlayerOutOfRange    = 3,
  PredictionThreshold = 4,
  Unsupported         = 5,
  NotSynchronize      = 6,
  InRollback          = 7,
  InputDropped        = 8,
  PlayerDisconnected  = 9,
  TooManySpectators   = 10,
  InvalidRequest      = 11
}

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
typedef struct {
   BackrollEventCode code;
   union {
      struct {
         BackrollPlayerHandle  player;
      } connected;
      struct {
         BackrollPlayerHandle  player;
         int               count;
         int               total;
      } synchronizing;
      struct {
         BackrollPlayerHandle  player;
      } synchronized;
      struct {
         BackrollPlayerHandle  player;
      } disconnected;
      struct {
         int               frames_ahead;
      } timesync;
      struct {
         BackrollPlayerHandle  player;
         int               disconnect_timeout;
      } connection_interrupted;
      struct {
         BackrollPlayerHandle  player;
      } connection_resumed;
   } u;
} BackrollEvent;

// The BackrollNetworkStats function contains some statistics about the current
// session.
public struct BackrollNetworkStats {
  // The length of the queue containing UDP packets which have not yet been
  // acknowledged by the end client.  The length of the send queue is a rough
  // indication of the quality of the connection.  The longer the send queue, the
  // higher the round-trip time between the clients. The send queue will also be
  // longer than usual during high packet loss situations.
  public int SendQueueLength;

  // The number of inputs currently buffered by the Backroll.net network layer
  // which have yet to be validated.  The length of the prediction queue is
  // roughly equal to the current frame number minus the frame number of the
  // last packet in the remote queue.
  public int ReceiveQueueLength;

  // The roundtrip packet transmission time as calcuated by Backroll.net.  This
  // will be roughly equal to the actual round trip packet transmission time + 2
  // the interval at which you call Idle or AdvanceFrame.
  public int Ping;

  // The estimated bandwidth used between the two clients, in kilobits per
  // second.
  public int KbpsSent;

  // The number of frames Backroll.net calculates that the local client is
  // behind the remote client at this instant in time.  For example, if at this
  // instant the current game client is running frame 1002 and the remote game
  // client is running frame 1009, this value will mostly likely roughly equal 7.
  public int LocalFramesBehind;

  // The same as LocalFramesBehind, but calculated from the perspective of the
  // remote player.
  public int RemoteFramesBehind;
}

public delegate void SaveGameStateCallback(void** buffer, int* len, int* checksum, int frame);
public delegate void LoadGameStateCallback(void* buffer, int len);
public delegate void LogGameStateCallback(string filename, void* buffer, int len);

public abstract class BackrollSession<T> : IDisposable where T : struct {

  public int InpuSize => UnsafeUtility.Sizeof<T>();

  protected BackrollSession() {
    Assert.IsTrue(UnsafeUtility.IsBlittable<T>());
  }

  // The client should allocate a buffer, copy the entire contents of the current
  // game state into it, and copy the length into the *len parameter.
  // Optionally, the client can compute a checksum of the data and store it in
  // the *checksum argument.
  public event SaveGameStateCallback OnSaveGameState;

  // Backroll.net will call this function at the beginning of a rollback.
  // The buffer and len parameters contain a previously saved state returned
  // from the save_game_state function.  The client should make the current game
  // state match the state contained in the buffer.
  public event LoadGameStateCallback OnLoadGameState;

  // Frees a game state allocated in OnSaveGameState.  You should deallocate the
  // memory contained in the buffer.
  public event Action<void*> OnFreeBuffer;

  // Called during a rollback.  You should advance your game
  // state by exactly one frame.  Before each frame, call ggpo_synchronize_input
  // to retrieve the inputs you should use for that frame.  After each frame,
  // you should call ggpo_advance_frame to notify Backroll.net that you're
  // finished.
  public event Action OnAdvanceFrame;

  // log_game_state - Used in diagnostic testing.  The client should use
  // the ggpo_log function to write the contents of the specified save
  // state in a human readible form.
  public event LogGameStateCallback OnLoadGameState;

  // on_event - Notification that something has happened.  See the
  // BackrollEventCode structure above for more information.
  public event Action<BackrollEvent> OnBackrollEvent;

  // Must be called for each player in the session (e.g. in a 3 player session, must
  // be called 3 times).
  //
  // player - A BackrollPlayer struct used to describe the player.
  //
  // Will raise a BackrollException if the state is invalid. Returns the handle
  // of the added player.
  public BackrollPlayerHandle AddPlayer(in BackrollPlayer player);

  // Change the amount of frames ggpo will delay local input.  Must be called
  // before the first call to SynchronizeInput.
  public BackrollErrorCode SetFrameDelay(BackrollPlayerHandle player,
                                         int frame_delay);

  // Should be called periodically by your application to give Backroll
  // a chance to do some work.  Most packet transmissions and rollbacks occur
  // in Idle.
  //
  // timeout - The amount of time Backroll.net is allowed to spend in this function,
  // in milliseconds.
  public BackrollErrorCode Idle(int timeout);

  // Used to close a session.  You must call this to free the resources allocated
  // in StartSession.
  public void Dispose();

  // You should call ggpo_synchronize_input before every frame of execution,
  // including those frames which happen during rollback.
  //
  // values - When the function returns, the values parameter will contain
  // inputs for this frame for all players.  The values array must be at
  // least (size * players) large.
  //
  // size - The size of the values array.
  //
  // disconnect_flags - Indicated whether the input in slot (1 << flag) is
  // valid.  If a player has disconnected, the input in the values array for
  // that player will be zeroed and the i-th flag will be set.  For example,
  // if only player 3 has disconnected, disconnect flags will be 8 (i.e. 1 << 3).
  BackrollErrorCode SynchronizeInput(void* values, int size, int *disconnect_flags);

  // Disconnects a remote player from a game.  Will return Backroll_ERRORCODE_PLAYER_DISCONNECTED
  // if you try to disconnect a player who has already been disconnected.
  BackrollErrorCode DisconnectPlayer(BackrollPlayerHandle player);

  // You should call this to notify Backroll that you have advancedt the
  // gamestate by a single frame.  You should call this everytime you advance the
  // gamestate by a frame, even during rollbacks.  Backroll may call your
  // save_state callback before this function returns.
  BackrollErrorCode AdvanceFrame();

  // Used to fetch some statistics about the quality of the network connection.
  //
  // player - The player handle returned from the ggpo_add_player function you used
  // to add the remote player.
  //
  // stats - Out parameter to the network statistics.
  BackrollNetworkStats GetNetworkStats(BackrollPlayerHandle player);

  // Sets the disconnect timeout.  The session will automatically disconnect
  // from a remote peer if it has not received a packet in the timeout window.
  // You will be notified of the disconnect via a Backroll_EVENTCODE_DISCONNECTED_FROM_PEER
  // event.
  //
  // Setting a timeout value of 0 will disable automatic disconnects.
  //
  // timeout - The time in milliseconds to wait before disconnecting a peer.
  BackrollErrorCode SetDisconnectTimeout(int timeout);

  // The time to wait before the first Backroll_EVENTCODE_NETWORK_INTERRUPTED timeout
  // will be sent.
  //
  // timeout - The amount of time which needs to elapse without receiving a packet
  //           before the Backroll_EVENTCODE_NETWORK_INTERRUPTED event is sent.
  BackrollErrorCode SetDisconnectNotifyStart(int timeout);
}

 public static class Backroll {

  // Starts a new Backroll session.
  //
  // num_players - The number of players which will be in this game.  The number
  // of players per session is fixed.  If you need to change the number of
  // players or any player disconnects, you must start a new session.
  //
  // local_port - The port Backroll should bind to for UDP traffic.
  static BackrollErrorCode StartSession(int num_players, LobbyBase lobby);

  // Start a spectator session.
  //
  // cb - A BackrollSessionCallbacks structure which contains the callbacks you implement
  // to help Backroll.net synchronize the two games.  You must implement all functions in
  // cb, even if they do nothing but 'return true';
  //
  // game - The name of the game.  This is used internally for Backroll for logging purposes only.
  //
  // num_players - The number of players which will be in this game.  The number of players
  // per session is fixed.  If you need to change the number of players or any player
  // disconnects, you must start a new session.
  static BackrollErrorCode StartSpectating(int num_players, LobbyBase lobby);

 // Used to being a new Backroll.net sync test session.  During a sync test, every
 // frame of execution is run twice: once in prediction mode and once again to
 // verify the result of the prediction.  If the checksums of your save states
 // do not match, the test is aborted.
 //
 // cb - A BackrollSessionCallbacks structure which contains the callbacks you implement
 // to help Backroll.net synchronize the two games.  You must implement all functions in
 // cb, even if they do nothing but 'return true';
 //
 // game - The name of the game.  This is used internally for Backroll for logging purposes only.
 //
 // num_players - The number of players which will be in this game.  The number of players
 // per session is fixed.  If you need to change the number of players or any player
 // disconnects, you must start a new session.
 //
 // frames - The number of frames to run before verifying the prediction.  The
 // recommended value is 1.
 static BackrollErrorCode StartSyncTest(int num_players, int frames);


}

}
