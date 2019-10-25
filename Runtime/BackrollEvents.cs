using System;

namespace HouraiTeahouse.Backroll {

public unsafe delegate void SaveGameStateCallback(ref Sync.SavedFrame frame);
public unsafe delegate void LoadGameStateCallback(void* buffer, int len);
public unsafe delegate void LogGameStateCallback(string filename, void* buffer, int len);

public class BackrollSessionCallbacks {

  public bool IsValid =>
    SaveGameState != null &&
    LoadGameState != null &&
    FreeBuffer != null &&
    AdvanceFrame != null;

  // Mandatory Callbacks - These must be implemented for Backroll to work.

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

  // Event Callbacks - Not required, but can be used to listen on Backroll events.

  // Handshake with the game running on the other side of the network has 
  // been completed.
  public Action<ConnectedEvent> OnConnected;

  public Action<ConnectionInterruptedEvent> OnConnectionInterrupted;

  public Action<ConnectionResumedEvent> OnConnectionResumed;

  // The network connection on the other end of the network has closed.
  public Action<DisconnectedEvent> OnDisconnected;

  // Beginning the synchronization process with the client on the other end 
  // of the networking.  The count and total fields in the event object 
  // indicate progress.
  public Action<PlayerSynchronizingEvent> OnPlayerSynchronizing;

  // The synchronziation with this peer has finished.
  public Action<PlayerSynchronizedEvent> OnPlayerSynchronized;

  // The synchronization for all peers has completed. 
  public Action OnReady;

  // The time synchronziation code has determined that this client is 
  // too far ahead of the other one and should slow down to ensure 
  // fairness.  The FramesAhead parameter jndicates how many frames 
  // the client is ahead.
  public Action<TimeSyncEvent> OnTimeSync;

  // Used only for sync tests, provides a way to log the state of a 
  // 1 - Prefix
  // 2 - Buffer Pointer
  // 3 - Buffer Size
  public Action<string, IntPtr, int> OnLogState;

}

public struct TimeSyncEvent {
   public int FramesAhead;
}

public struct ConnectionInterruptedEvent {
   public BackrollPlayerHandle  Player;
   public uint DisconnectTimeout;
}

public struct ConnectionResumedEvent {
   public BackrollPlayerHandle  Player;
}

public struct ConnectedEvent {
   public BackrollPlayerHandle  Player;
}

public struct DisconnectedEvent {
   public BackrollPlayerHandle  Player;
}

public struct PlayerSynchronizingEvent {
   public BackrollPlayerHandle  Player;
   public int Count;
   public int Total;
}

public struct PlayerSynchronizedEvent {
   public BackrollPlayerHandle  Player;
}

}
