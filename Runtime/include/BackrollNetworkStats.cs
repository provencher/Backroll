namespace HouraiTeahouse.Backroll {

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

}
