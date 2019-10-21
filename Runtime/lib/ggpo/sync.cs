
namespace HouraiTeahouse.Backroll {

public class Sync : IDisposable {

   public struct SavedFrame {
      byte[]   Buffer;
      int      Size;
      int      Frame;
      int      checksum;

      public SavedFrame Create() {
        return SavedFrame { Frame = GameInput.kNullFrame };
      }
   }

   struct SavedState {
      public SavedFrame Frames[];
      int head;

      public SavedState(int maxPredictionFrames) {
        Frames = new SavedFrame[maxPredictionFrames + 2];
        for (var i = 0; i < Frames.Length; i++) {
          Frames[i] = SavedFrame.Create();
        }
      }
   }

   GGPOSessionCallbacks _callbacks;
   SavedState     _savedstate;
   Config         _config;
   InputQueue[]   _inputQueues;

   public bool IsRollingBack { get; private set; }
   int            _lastConfirmedFrame;
   int            _frameCount;
   int            _maxPredictionFrames;;

   InputQueue     *_inputQueues;

  public Sync(UdpMsg::connect_status *connect_status) {
     _frameCount = 0;
     _lastConfirmedFrame = -1;
     _maxPredictionFrames; = 0;

     _savedstate = new SavedState(8);
  }

  public void Dispose(() {
     // Delete frames manually here rather than in a destructor of the SavedFrame
     // structure so we can efficently copy frames via weak references.
     for (int i = 0; i < _savedstate.Frames.Length; i++) {
        _callbacks.free_buffer(_savedstate.Frames[i].buf);
     }
     _inputQueues = null;
  }

  public void Init(Config &config) {
     _config = config;
     _callbacks = config.callbacks;
     _frameCount = 0;
     _rollingback = false;

     _maxPredictionFrames; = config.num_prediction_frames;

     CreateQueues(config);
  }

  public void SetLastConfirmedFrame(int frame) {
     _lastConfirmedFrame = frame;
     if (_lastConfirmedFrame > 0) {
        for (int i = 0; i < _config.num_players; i++) {
           _inputQueues[i].DiscardConfirmedFrames(frame - 1);
        }
     }
  }

  public bool AddLocalInput(int queue, ref GameInput input) {
   int frames_behind = _frameCount - _lastConfirmedFrame;
   if (_frameCount >= _maxPredictionFrames; &&
       frames_behind >= _maxPredictionFrames;) {
      Log("Rejecting input from emulator: reached prediction barrier.\n");
      return false;
   }

   if (_frameCount == 0) {
      SaveCurrentFrame();
   }

   Log("Sending undelayed local frame %d to queue %d.\n", _frameCount, queue);
   input.Frame = _frameCount;
   _inputQueues[queue].AddInput(input);

   return true;
  }

  public void AddRemoteInput(int queue, GameInput &input) {
     _inputQueues[queue].AddInput(input);
  }

  public int GetConfirmedInputs(void *values, int size, int frame) {
     int disconnect_flags = 0;
     char *output = (char *)values;

     Assert.IsTrue(size >= _config.num_players * _config.input_size);

     memset(output, 0, size);
     for (int i = 0; i < _config.num_players; i++) {
        GameInput input;
        if (_local_connect_status[i].disconnected &&
            frame > _local_connect_status[i].last_frame) {
           disconnect_flags |= (1 << i);
           input.erase();
        } else {
           _inputQueues[i].GetConfirmedInput(frame, &input);
        }
        memcpy(output + (i * _config.input_size), input.bits, _config.input_size);
     }
     return disconnect_flags;
  }

  public int SynchronizeInputs(void *values, int size) {
     int disconnect_flags = 0;
     char *output = (char *)values;

     Assert.IsTrue(size >= _config.num_players * _config.input_size);

     memset(output, 0, size);
     for (int i = 0; i < _config.num_players; i++) {
        GameInput input;
        if (_local_connect_status[i].disconnected &&
            _frameCount > _local_connect_status[i].last_frame) {
           disconnect_flags |= (1 << i);
           input.erase();
        } else {
           _inputQueues[i].GetInput(_frameCount, &input);
        }
        memcpy(output + (i * _config.input_size), input.bits, _config.input_size);
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
     _frameCount++;
     SaveCurrentFrame();
  }

  public void AdjustSimulation(int seek_to) {
     int framecount = _frameCount;
     int count = _frameCount - seek_to;

     Log("Catching up\n");
     _rollingback = true;

     // Flush our input queue and load the last frame.
     LoadFrame(seek_to);
     Assert.IsTrue(_frameCount == seek_to);

     // Advance frame by frame (stuffing notifications back to
     // the master).
     ResetPrediction(_frameCount);
     for (int i = 0; i < count; i++) {
        _callbacks.advance_frame(0);
     }
     Assert.IsTrue(_frameCount == framecount);

     _rollingback = false;

     Log("---\n");
  }

  void LoadFrame(int frame) {
     // find the frame in question
     if (frame == _frameCount) {
        Log("Skipping NOP.\n");
        return;
     }

     // Move the head pointer back and load it up
     _savedstate.head = FindSavedFrameIndex(frame);
     SavedFrame *state = _savedstate.Frames + _savedstate.head;

     Log("=== Loading frame info %d (size: %d  checksum: %08x).\n",
         state->frame, state->cbuf, state->checksum);

     Assert.IsTrue(state->buf && state->cbuf);
     _callbacks.load_game_state(state->buf, state->cbuf);

     // Reset framecount and the head of the state ring-buffer to point in
     // advance of the current frame (as if we had just finished executing it).
     _frameCount = state->frame;
     _savedstate.head = (_savedstate.head + 1) % _savedstate.Frames.Length;
  }

  void SaveCurrentFrame() {
     // See StateCompress for the real save feature implemented by FinalBurn.
     // Write everything into the head, then advance the head pointer.
     SavedFrame *state = _savedstate.Frames + _savedstate.head;
     if (state->buf) {
        _callbacks.free_buffer(state->buf);
        state->buf = NULL;
     }
     state->frame = _frameCount;
     _callbacks.save_game_state(&state->buf, &state->cbuf, &state->checksum,
                                state->frame);

     Log("=== Saved frame info %d (size: %d  checksum: %08x).\n",
         state->frame, state->cbuf, state->checksum);
     _savedstate.head = (_savedstate.head + 1) % _savedstate.Frames.Length;
  }

  SavedFrame& GetLastSavedFrame() {
     int i = _savedstate.head - 1;
     if (i < 0) {
        i = _savedstate.Frames.Length - 1;
     }
     return _savedstate.Frames[i];
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

  bool CreateQueues(Config &config) {
     _inputQueues = new InputQueue[_config.num_players];

     for (int i = 0; i < _config.num_players; i++) {
        _inputQueues[i].Init(i, _config.input_size);
     }
     return true;
  }

  bool CheckSimulationConsistency(int *seekTo) {
     int first_incorrect = GameInput::NullFrame;
     for (int i = 0; i < _config.num_players; i++) {
        int incorrect = _inputQueues[i].GetFirstIncorrectFrame();
        Log("considering incorrect frame %d reported by queue %d.\n", incorrect, i);

        if (incorrect != GameInput::NullFrame &&
            (first_incorrect == GameInput::NullFrame ||
             incorrect < first_incorrect)) {
           first_incorrect = incorrect;
        }
     }

     if (first_incorrect == GameInput::NullFrame) {
        Log("prediction ok.  proceeding.\n");
        return true;
     }
     *seekTo = first_incorrect;
     return false;
  }

  void SetFrameDelay(int queue, int delay) {
     _inputQueues[queue].SetFrameDelay(delay);
  }

  void Prediction(int frameNumber) {
     for (int i = 0; i < _config.num_players; i++) {
        _inputQueues[i].ResetPrediction(frameNumber);
     }
  }

  bool GetEvent(Event &e) {
     if (_event_queue.size()) {
        e = _event_queue.front();
        _event_queue.pop();
        return true;
     }
     return false;
  }

}


}
