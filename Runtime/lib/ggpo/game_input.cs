using System;
using System.Text;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Assertions;

namespace HouraiTeahouse.Backroll {

public unsafe struct GameInput {

  public const int kNullFrame = -1;

  public const int kMaxPlayers = 8;
  public const int kMaxBytes   = 8;

  public int Frame;
  public uint Size;
  public fixed byte bits[kMaxPlayers * kMaxBytes];

  public GameInput(int iframe, void* ibits, uint isize) {
     Assert.IsTrue(isize > 0);
     Assert.IsTrue(isize <= kMaxBytes * kMaxPlayers);
     Frame = iframe;
     Size = isize;
     fixed (byte* ptr = bits) {
       UnsafeUtility.MemClear(ptr, kMaxBytes * kMaxPlayers);
       if (ibits != null) {
          UnsafeUtility.MemCpy(ptr, ibits, isize);
       }
     }
  }

  public GameInput(int frame, void* ibits, uint isize, uint offset) {
     Assert.IsTrue(isize > 0);
     Assert.IsTrue(isize <= kMaxBytes);
     Frame = frame;
     Size = isize;
     fixed (byte* ptr = bits) {
      UnsafeUtility.MemClear(ptr, kMaxPlayers * kMaxBytes);
      if (ibits != null) {
          UnsafeUtility.MemCpy(ptr + (offset * isize), ibits, isize);
      }
     }
  }

  public static GameInput Create<T>(int iframe, ref T value, uint offset = 0) where T : struct {
    var size = UnsafeUtility.SizeOf<T>();
    var input = new GameInput(iframe, null, (uint)size);
    UnsafeUtility.CopyStructureToPtr(ref value, input.bits + size * offset);
    return input;
  }

  public bool IsNull => Frame == kNullFrame;

  public void Clear() {
    fixed (byte* ptr = bits) {
      UnsafeUtility.MemClear(ptr, Size);
    }
  }

  public string ToString(bool show_frame = true) {
     Assert.IsTrue(Size > 0);
     string retVal;
     if (show_frame) {
       retVal = $"(frame:{Frame} size:{Size} ";
     } else {
       retVal = $"(size:{Size} ";
     }
     var builder = new StringBuilder(retVal);
     for (var i = 0; i < Size; i++) {
       builder.AppendFormat("{0:x2}", bits[Size]);
     }
     builder.Append(")");
     return builder.ToString();
  }

  public void Log(string prefix, bool show_frame = true) {
    Debug.Log(prefix + ToString(show_frame));
  }

  public bool Equals(in GameInput other, bool bitsonly) {
    if (!bitsonly && Frame != other.Frame) {
      Debug.LogFormat("frames don't match: {}, {}", Frame, other.Frame);
    }
    if (Size != other.Size) {
      Debug.LogFormat("sizes don't match: {}, {}", Size, other.Size);
    }
    fixed (byte* ptr = bits, otherPtr = other.bits) {
      if (UnsafeUtility.MemCmp(ptr, otherPtr, Size) != 0) {
        Debug.Log("bits don't match");
      }
      Assert.IsTrue(Size > 0 && other.Size > 0);
      return (bitsonly || Frame == other.Frame) &&
              Size == other.Size &&
              UnsafeUtility.MemCmp(ptr, otherPtr, Size) == 0;
    }
  }

}


}
