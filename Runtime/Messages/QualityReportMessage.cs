using HouraiTeahouse.Networking;
using UnityEngine;

namespace HouraiTeahouse.Backroll {

public struct QualityReportMessage : INetworkSerializable {
  public byte FrameAdvantage;
  public uint Ping;

  public void Serialize(ref Serializer serializer)  {
    serializer.Write(FrameAdvantage);
    serializer.Write(Ping);
  }

  public void Deserialize(ref Deserializer deserializer) {
    FrameAdvantage = deserializer.ReadByte();
    Ping = deserializer.ReadUInt32();
  }
}

}