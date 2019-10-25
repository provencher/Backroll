using System;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Assertions;

namespace HouraiTeahouse.Backroll {

public unsafe class Sync : IDisposable {

   public struct SavedFrame {
      public byte*   Buffer;
      public int     Size;
      public int     Frame;
      public int     Checksum;

      public static SavedFrame Create() {
        return new SavedFrame { Frame = GameInput.kNullFrame };
      }
   }

   struct SavedState {
      public SavedFrame[] Frames;
      public int Head;

      public SavedState(int maxPredictionFrames) {
        Head = 0;
        Frames = new SavedFrame[maxPredictionFrames + 2];
        for (var i = 0; i < Frames.Length; i++) {
          Frames[i] = SavedFrame.Create();
        }
      }
   }

   public struct Config {
      public int NumPlayers;
      public int InputSize;
      public int NumPredictionFrames;
      public BackrollSessionCallbacks Callbacks;
   }

   BackrollSessionCallbacks _callbacks;
   SavedState     _savedstate;
   Config         _config;
   InputQueue[]   _inputQueues;
   BackrollConnectStatus[] _localConnectStatus;

   public bool    InRollback { get; private set; }
   public int     FrameCount { get; private set; }
   int            _lastConfirmedFrame;
   int            _maxPredictionFrames;

  public Sync(BackrollConnectStatus[] connect_status, Config config) {
     _localConnectStatus = connect_status;
     _lastConfirmedFrame = -1;
     _maxPredictionFrames = 0;

     _savedstate = new SavedState(8);
     _config = config;
     _callbacks = config.Callbacks;
     FrameCount = 0;
     InRollback = false;

     _maxPredictionFrames = config.NumPredictionFrames;

     CreateQueues(config);
  }

  public void Dispose() {
     // Delete frames manually here rather than in a destructor of the SavedFrame
     // structure so we can efficently copy frames via weak references.
     for (int i = 0; i < _savedstate.Frames.Length; i++) {
       _callbacks.FreeBuffer?.Invoke((IntPtr)_savedstate.Frames[i].Buffer);
     }
     _inputQueues = null;
  }

  public void SetLastConfirmedFrame(int frame) {
     _lastConfirmedFrame = frame;
     if (_lastConfirmedFrame > 0) {
        for (int i = 0; i < _config.NumPlayers; i++) {
           _inputQueues[i].DiscardConfirmedFrames(frame - 1);
        }
     }
  }

  public bool AddLocalInput(int queue, ref GameInput input) {
   int frames_behind = FrameCount - _lastConfirmedFrame;
   if (FrameCount >= _maxPredictionFrames &&
       frames_behind >= _maxPredictionFrames) {
      Debug.Log("Rejecting input from emulator: reached prediction barrier.");
      return false;
   }

   if (FrameCount == 0) {
      SaveCurrentFrame();
   }

   Debug.LogFormat("Sending undelayed local frame {} to queue {}.", FrameCount, queue);
   input.Frame = FrameCount;
   _inputQueues[queue].AddInput(ref input);

   return true;
  }

  public void AddRemoteInput(int queue, ref GameInput input) {
     _inputQueues[queue].AddInput(ref input);
  }

  public int GetConfirmedInputs(void* values, int size, int frame) {
     int disconnect_flags = 0;
     var output = (byte*)values;

     Assert.IsTrue(size >= _config.NumPlayers * _config.InputSize);

     UnsafeUtility.MemClear(values, size);
     for (int i = 0; i < _config.NumPlayers; i++) {
        var input = new GameInput(GameInput.kNullFrame, null, (uint)_config.InputSize);
        if (_localConnectStatus[i].Disconnected &&
            frame > _localConnectStatus[i].LastFrame) {
           disconnect_flags |= (1 << i);
        } else {
           _inputQueues[i].GetConfirmedInput(frame, ref input);
        }
        UnsafeUtility.MemCpy(output + (i * _config.InputSize), input.bits, _config.InputSize);
     }
     return disconnect_flags;
  }

  public int SynchronizeInputs(void *values, int size) {
     int disconnect_flags = 0;
     var output = (byte*)values;

     Assert.IsTrue(size >= _config.NumPlayers * _config.InputSize);

     UnsafeUtility.MemClear(values, size);
     for (int i = 0; i < _config.NumPlayers; i++) {
        var input = new GameInput(GameInput.kNullFrame, null, (uint)_config.InputSize);
        if (_localConnectStatus[i].Disconnected &&
            FrameCount > _localConnectStatus[i].LastFrame) {
           disconnect_flags |= (1 << i);
        } else {
           _inputQueues[i].GetInput(FrameCount, out input);
        }
        UnsafeUtility.MemCpy(output + (i * _config.InputSize), input.bits, _config.InputSize);
     }
     return disconnect_flags;
  }

  public void CheckSimulation(int timeout) {
     int seek_to;
     if (!CheckSimulationConsistency(&seek_to)) {
        AdjustSimulation(seek_to);
     }
  }

  public void IncrementFrame() {
     FrameCount++;
     SaveCurrentFrame();
  }

  public void AdjustSimulation(int seek_to) {
     int framecount = FrameCount;
     int count = FrameCount - seek_to;

     Debug.Log("Catching up");
     InRollback = true;

     // Flush our input queue and load the last frame.
     LoadFrame(seek_to);
     Assert.IsTrue(FrameCount == seek_to);

     // Advance frame by frame (stuffing notifications back to
     // the master).
     ResetPrediction(FrameCount);
     for (int i = 0; i < count; i++) {
        _callbacks.AdvanceFrame();
     }
     Assert.IsTrue(FrameCount == framecount);

     InRollback = false;

     Debug.Log("---");
  }

  protected void ResetPrediction(int frameNumber) {
    for (int i = 0; i < _inputQueues.Length; i++) {
      _inputQueues[i].ResetPrediction(frameNumber);
    }
  }

  public void LoadFrame(int frame) {
     // find the frame in question
     if (frame == FrameCount) {
        Debug.Log("Skipping NOP.");
        return;
     }

     // Move the Head pointer back and load it up
     _savedstate.Head = FindSavedFrameIndex(frame);
     ref SavedFrame state = ref _savedstate.Frames[_savedstate.Head];

     Debug.LogFormat("=== Loading frame info {} (size: {}  checksum: %08x).",
         state.Frame, state.Size, state.Checksum);

     Assert.IsTrue(state.Buffer != null && state.Size != 0);
     _callbacks.LoadGameState(state.Buffer, state.Size);

     // Reset framecount and the Head of the state ring-buffer to point in
     // advance of the current frame (as if we had just finished executing it).
     FrameCount = state.Frame;
     _savedstate.Head = (_savedstate.Head + 1) % _savedstate.Frames.Length;
  }

  public void SaveCurrentFrame() {
     // See StateCompress for the real save feature implemented by FinalBurn.
     // Write everything into the Head, then advance the Head pointer.
     ref SavedFrame state = ref _savedstate.Frames[_savedstate.Head];
     if (state.Buffer != null) {
        _callbacks.FreeBuffer((IntPtr)state.Buffer);
        state.Buffer = null;
     }
     _callbacks.SaveGameState(ref state);
     state.Frame = FrameCount;

     Debug.LogFormat("=== Saved frame info {} (size: {}  checksum: %08x).",
         state.Frame, state.Size, state.Checksum);

     _savedstate.Head = (_savedstate.Head + 1) % _savedstate.Frames.Length;
  }

  public ref SavedFrame GetLastSavedFrame() {
     int i = _savedstate.Head - 1;
     if (i < 0) {
        i = _savedstate.Frames.Length - 1;
     }
     return ref _savedstate.Frames[i];
  }

  int FindSavedFrameIndex(int frame) {
     int i, count = _savedstate.Frames.Length;
     for (i = 0; i < count; i++) {
        if (_savedstate.Frames[i].Frame == frame) {
           break;
        }
     }
     Assert.IsTrue(i != count);
     return i;
  }

  bool CreateQueues(in Config config) {
     _inputQueues = new InputQueue[_config.NumPlayers];
     for (int i = 0; i < _config.NumPlayers; i++) {
        _inputQueues[i] = new InputQueue(128, (uint)_config.InputSize, i);
     }
     return true;
  }

  bool CheckSimulationConsistency(int *seekTo) {
     int first_incorrect = GameInput.kNullFrame;
     for (int i = 0; i < _config.NumPlayers; i++) {
        int incorrect = _inputQueues[i].GetFirstIncorrectFrame();
        Debug.LogFormat("considering incorrect frame {} reported by queue {}.", incorrect, i);

        if (incorrect != GameInput.kNullFrame &&
            (first_incorrect == GameInput.kNullFrame ||
             incorrect < first_incorrect)) {
           first_incorrect = incorrect;
        }
     }

     if (first_incorrect == GameInput.kNullFrame) {
        Debug.Log("prediction ok.  proceeding.");
        return true;
     }
     *seekTo = first_incorrect;
     return false;
  }

  public void SetFrameDelay(int queue, int delay) {
     _inputQueues[queue].FrameDelay = delay;
  }

  void Prediction(int frameNumber) {
     for (int i = 0; i < _config.NumPlayers; i++) {
        _inputQueues[i].ResetPrediction(frameNumber);
     }
  }

}


}
