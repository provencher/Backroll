using HouraiTeahouse.Networking;

namespace HouraiTeahouse.Backroll {

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

}
