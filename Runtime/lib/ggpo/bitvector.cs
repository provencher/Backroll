using UnityEngine.Assertions;

namespace HouraiTeahouse.Backroll {

public static unsafe class BitVector {

  public const int kNibbleSize = 8;

  public static void SetBit(byte* vector, ref int offset) {
     vector[offset / 8] |= (byte)(1 << (offset % 8));
     offset += 1;
  }

  public static void ClearBit(byte* vector, ref int offset) {
     vector[offset / 8] &= (byte)~(1 << (offset % 8));
     offset += 1;
  }

  public static void WriteNibblet(byte* vector, int nibble, ref int offset) {
     Assert.IsTrue(nibble < (1 << kNibbleSize));
     for (int i = 0; i < kNibbleSize; i++) {
        if ((nibble & (1 << i)) != 0) {
           SetBit(vector, ref offset);
        } else {
           ClearBit(vector, ref offset);
        }
     }
  }

  public static bool ReadBit(byte* vector, ref int offset) {
     bool retval = (vector[offset / 8] & (1 << (offset % 8))) != 0;
     offset += 1;
     return retval;
  }

  public static int ReadNibblet(byte* vector, ref int offset) {
     int nibblet = 0;
     for (int i = 0; i < kNibbleSize; i++) {
        nibblet |= ((ReadBit(vector, ref offset) ? 1 : 0) << i);
     }
     return nibblet;
  }

}

}
