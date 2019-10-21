using System;
using System.Text;
using UnityEngine.Assertions;

namespace HouraiTeahouse.Backroll {

public unsafe struct GameInput : IDisposable {

  public const int kNullFrame = -1;

  public const int kMaxPlayers = 8;
  public const int kMaxBytes   = 8;

  public int Frame;
  public int Size;
  public fixed byte bits[kMaxPlayers * kMaxBytes];

  public GameInput(int frame, void* ibits, uint isize, uint offset) {
     Assert.IsTrue(isize);
     Assert.IsTrue(isize <= kMaxBytes);
     Frame = iframe;
     Size = isize;
     UnsafeUtility.MemClear(bits, 0, kMaxPlayers * kMaxBytes);
     if (ibits != 0) {
        UnsafeUtility.MemCpy(bits + (offset * isize), ibits, isize);
     }
  }

  public GameInput(int iframe, void* ibits, uint isize) {
     Assert.IsTrue(isize);
     Assert.IsTrue(isize <= kMaxBytes * kMaxPlayers);
     Frame = iframe;
     Size = isize;
     UnsafeUtility.MemClear(bits, 0, kMaxBytes * kMaxPlayers);
     if (ibits != 0) {
        UnsafeUtility.MemCpy(bits, ibits, isize);
     }
  }

  public bool IsNull => Frame == kNullFrame;

  public void Dispose() => UnsafeUtility.Free(bits,

  public void Clear() => UnsafeUtility.MemClear(bits, Size);

  public string ToString(bool show_frame = true) {
     Assert.IsTrue(size);
     string base;
     if (show_frame) {
       base = $"(frame:{Frame} size:{Size} ";
     } else {
       base = $"(size:{Size} ";
     }
     var builder = new StringBuilder(base);
     for (var i = 0; i < Size; i++) {
       builder.AppendFormat("{0:x2}", bits[Size]);
     }
     builder.Append(")");
  }

  public void Log(string prefix, bool show_frame = true) {
    Debug.Log(prefix + ToString(show_frame));
  }

  public bool Equals(in GameInput other, bool bitsonly) {
    if (!bitsonly && Frame != other.Frame) {
      Debug.LogFormat("frames don't match: {}, {}", frame, other.frame);
    }
    if (Size != other.Size) {
      Debug.LogFormat("sizes don't match: {}, {}", size, other.size);
    }
    if (UnsafeUtility.MemCmp(bits, other.bits, Size) != 0) {
      Debug.Log("bits don't match");
    }
    Assert.IsTrue(Size > 0 && other.Size > 0);
    return (bitsonly || Frame == other.Frame) &&
            Size == other.Size &&
            UnsafeUtility.MemCmp(bits, other.bits, Size) == 0;
  }

}


}
