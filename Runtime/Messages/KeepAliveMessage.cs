using HouraiTeahouse.Networking;

namespace HouraiTeahouse.Backroll {

public struct SyncReplyMessage : INetworkSerializable {
  public uint RandomReply;

  public void Serialize(ref Serializer serializer) =>
    serializer.Write(RandomReply);
  public void Deserialize(ref Deserializer deserializer) =>
    RandomReply = deserializer.ReadUInt32();
}

}