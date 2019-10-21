using UnityEngine.Assertions;

namespace HouraiTeahouse.Backroll {

public static unsafe class BitVector {

  public const int kNibbleSize = 8;

  public void SetBit(byte* vector, ref int offset) {
     vector[offset / 8] |= (1 << (offset % 8));
     offset += 1;
  }

  public  void ClearBit(byte* vector, ref int offset) {
     vector[offset / 8] &= ~(1 << (offset % 8));
     offset += 1;
  }

  public void WriteNibblet(byte* vector, int nibble, ref int offset) {
     Assert.IsTrue(nibble < (1 << kNibbleSize));
     for (int i = 0; i < kNibbleSize; i++) {
        if (nibble & (1 << i)) {
           SetBit(vector, ref offset);
        } else {
           ClearBit(vector, ref offset);
        }
     }
  }

  public int ReadBit(byte* vector, ref int offset) {
     int retval = !!(vector[offset / 8] & (1 << (offset % 8)));
     offset += 1;
     return retval;
  }

  public int ReadNibblet(uint8 *vector, int *offset) {
     int nibblet = 0;
     for (int i = 0; i < kNibbleSize; i++) {
        nibblet |= (ReadBit(vector, offset) << i);
     }
     return nibblet;
  }

}

}
