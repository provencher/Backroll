using HouraiTeahouse.Networking;
using UnityEngine;
using UnityEngine.Assertions;
using Unity.Collections.LowLevel.Unsafe;

namespace HouraiTeahouse.Backroll {

public unsafe struct InputMessage : INetworkSerializable {

   public const int kMaxCompressedBits = 4096;

   static InputMessage() {
      Assert.IsTrue(UnsafeUtility.SizeOf<BackrollConnectStatus>() == sizeof(uint));
   }

   public fixed uint                 connect_status[BackrollConstants.kMaxPlayers];
   public int                        StartFrame;

   public bool                        DisconnectRequested;
   public int                         AckFrame;

   public ushort                      NumBits;
   public uint                        InputSize; // XXX: shouldn't be in every single packet!
   public fixed byte                  bits[kMaxCompressedBits / 8]; /* must be last */

  public void Serialize(ref Serializer serializer) {
     fixed (uint* status = connect_status) {
      for (var i = 0; i < BackrollConstants.kMaxPlayers; i++) {
         // Doable since BackrollConnectionStatus is exactly the size of
         // a uint32.
         ((BackrollConnectStatus*)status)[i].Serialize(ref serializer);
      }
     }
     serializer.Write(StartFrame);
     serializer.Write(DisconnectRequested);
     serializer.Write(NumBits);
     serializer.Write(AckFrame);
     serializer.Write(InputSize);
     fixed (byte* ptr = bits) {
        serializer.Write(ptr, (ushort)Mathf.CeilToInt(NumBits / 8f));
     }
  }

  public void Deserialize(ref Deserializer deserializer) {
     fixed (uint* status = connect_status) {
      for (var i = 0; i < BackrollConstants.kMaxPlayers; i++) {
         // Doable since BackrollConnectionStatus is exactly the size of
         // a uint32.
         ((BackrollConnectStatus*)status)[i].Deserialize(ref deserializer);
      }
     }
     StartFrame = deserializer.ReadInt32();
     DisconnectRequested = deserializer.ReadBoolean();
     NumBits = deserializer.ReadUInt16();
     AckFrame = deserializer.ReadInt32();
     InputSize = deserializer.ReadUInt16();
     fixed (byte* ptr = bits) {
        deserializer.ReadBytes(ptr, (ushort)Mathf.CeilToInt(NumBits / 8f));
     }
  }
}
}