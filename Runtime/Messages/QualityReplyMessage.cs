using HouraiTeahouse.Networking;

namespace HouraiTeahouse.Backroll {

public struct QualityReplyMessage : INetworkSerializable {
  public uint Pong;

  public void Serialize(ref Serializer serializer) =>
    serializer.Write(Pong);
  public void Deserialize(ref Deserializer deserializer) =>
    Pong = deserializer.ReadUInt32();
}

}