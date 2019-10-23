using HouraiTeahouse.Networking;

namespace HouraiTeahouse.Backroll {

public struct KeepAliveMessage : INetworkSerializable {
  public void Serialize(ref Serializer serializer) {}
  public void Deserialize(ref Deserializer deserializer) {}
}

}