using HouraiTeahouse.Networking;

namespace HouraiTeahouse.Backroll {

public struct InputAckMessage : INetworkSerializable {
  public int AckFrame;

  public void Serialize(ref Serializer serializer) =>
    serializer.Write(AckFrame);
  public void Deserialize(ref Deserializer deserializer) =>
    AckFrame = deserializer.ReadInt32();
}

}