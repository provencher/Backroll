using HouraiTeahouse.Networking;

namespace HouraiTeahouse.Backroll {

public struct SyncRequestMessage : INetworkSerializable {
  public uint RandomRequest;
  public byte RemoteEndpoint;

  public void Serialize(ref Serializer serializer) {
    serializer.Write(RandomRequest);
    serializer.Write(RemoteEndpoint);
  }
  public void Deserialize(ref Deserializer deserializer) {
    RandomRequest = deserializer.ReadUInt32();
    RemoteEndpoint = deserializer.ReadByte();
  }
}

}