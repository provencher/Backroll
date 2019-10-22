using UnityEngine.Assertions;

namespace HouraiTeahouse.Backroll {

public class RingBuffer<T> {

  readonly T[] _data;

  public int Size { get; private set; }

  int _head, _tail;

  public RingBuffer(int size) {
    _data = new T[size];
    _head = _tail = 0;
  }

  public ref T Peek() {
    Assert.IsTrue(Size != _data.Length);
    return ref _data[_tail];
  }

  public ref T this[int idx] => ref _data[(_tail + idx) % _data.Length];

  public void Pop() {
    Assert.IsTrue(Size != _data.Length);
    _tail = (_tail + 1) % _data.Length;
    Size--;
  }

  public void Push(in T val) {
    Assert.IsTrue(Size != (_data.Length - 1));
    _data[_head] = val;
    _head = (_head + 1) % _data.Length;
    Size++;
  }

  public bool IsEmpty() => Size == 0;

}

}