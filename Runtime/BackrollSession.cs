using System;
using HouraiTeahouse.Networking;
using HouraiTeahouse.Networking.Topologies;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine.Assertions;

namespace HouraiTeahouse.Backroll {

public unsafe delegate void SaveGameStateCallback(void** buffer, int* len, int* checksum, int frame);
public unsafe delegate void LoadGameStateCallback(void* buffer, int len);
public unsafe delegate void LogGameStateCallback(string filename, void* buffer, int len);

public struct BackrollSessionCallbacks {
  // The client should allocate a buffer, copy the entire contents of the current
  // game state into it, and copy the length into the *len parameter.
  // Optionally, the client can compute a checksum of the data and store it in
  // the *checksum argument.
  public SaveGameStateCallback SaveGameState;

  // Backroll.net will call this function at the beginning of a rollback.
  // The buffer and len parameters contain a previously saved state returned
  // from the save_game_state function.  The client should make the current game
  // state match the state contained in the buffer.
  public LoadGameStateCallback LoadGameState;

  // Frees a game state allocated in SaveGameState.  You should deallocate the
  // memory contained in the buffer.
  public Action<IntPtr> FreeBuffer;

  // Called during a rollback.  You should advance your game
  // state by exactly one frame.  Before each frame, call ggpo_synchronize_input
  // to retrieve the inputs you should use for that frame.  After each frame,
  // you should call ggpo_advance_frame to notify Backroll.net that you're
  // finished.
  public Action AdvanceFrame;

  // log_game_state - Used in diagnostic testing.  The client should use
  // the ggpo_log function to write the contents of the specified save
  // state in a human readible form.
  public LogGameStateCallback LogGameState;

  // on_event - Notification that something has happened.  See the
  // BackrollEventCode structure above for more information.
  public Action<BackrollEvent> BackrollEvent;
}

public static class Backroll {

  // Starts a new Backroll session.
  //
  // num_players - The number of players which will be in this game.  The number
  // of players per session is fixed.  If you need to change the number of
  // players or any player disconnects, you must start a new session.
  //
  // local_port - The port Backroll should bind to for UDP traffic.
  public static BackrollSession<T> StartSession<T>(BackrollSessionConfig config) where T : struct {
    return new P2PBackrollSession<T>(config);
  }

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
  public static BackrollSession<T> StartSpectating<T>(LobbyBase lobby) where T : struct {
    throw new NotImplementedException();
  }

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
 public static BackrollSession<T> StartSyncTest<T>(int frames) where T : struct {
    throw new NotImplementedException();
 }

}

public abstract class BackrollSession<T> : FullMeshPeer where T : struct {

  public static int InputSize => UnsafeUtility.SizeOf<T>();

  protected BackrollSession(LobbyBase lobby) : base(lobby) {
    Assert.IsTrue(UnsafeUtility.IsBlittable<T>());
  }

  // Change the amount of frames ggpo will delay local input.  Must be called
  // before the first call to SynchronizeInput.
  public abstract void SetFrameDelay(BackrollPlayerHandle player,
                                     int frame_delay);

  // Should be called periodically by your application to give Backroll
  // a chance to do some work.  Most packet transmissions and rollbacks occur
  // in Idle.
  //
  // timeout - The amount of time Backroll.net is allowed to spend in this function,
  // in milliseconds.
  public abstract void Idle(int timeout);

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
  public unsafe abstract int SyncInput(void* values, int size);

  // Disconnects a remote player from a game.  Will return Backroll_ERRORCODE_PLAYER_DISCONNECTED
  // if you try to disconnect a player who has already been disconnected.
  public abstract void DisconnectPlayer(BackrollPlayerHandle player);

  // You should call this to notify Backroll that you have advancedt the
  // gamestate by a single frame.  You should call this everytime you advance the
  // gamestate by a frame, even during rollbacks.  Backroll may call your
  // save_state callback before this function returns.
  public abstract void AdvanceFrame();

  // Used to fetch some statistics about the quality of the network connection.
  //
  // player - The player handle returned from the ggpo_add_player function you
  // used to add the remote player.
  //
  // Returns the network statistics.
  public abstract BackrollNetworkStats GetNetworkStats(BackrollPlayerHandle player);

  // Sets the disconnect timeout.  The session will automatically disconnect
  // from a remote peer if it has not received a packet in the timeout window.
  // You will be notified of the disconnect via a Backroll_EVENTCODE_DISCONNECTED_FROM_PEER
  // event.
  //
  // Setting a timeout value of 0 will disable automatic disconnects.
  //
  // timeout - The time in milliseconds to wait before disconnecting a peer.
  public abstract void SetDisconnectTimeout(int timeout);

  // The time to wait before the first Backroll_EVENTCODE_NETWORK_INTERRUPTED timeout
  // will be sent.
  //
  // timeout - The amount of time which needs to elapse without receiving a packet
  //           before the Backroll_EVENTCODE_NETWORK_INTERRUPTED event is sent.
  public abstract void SetDisconnectNotifyStart(int timeout);
}

}
