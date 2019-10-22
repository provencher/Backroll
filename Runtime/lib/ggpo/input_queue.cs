using UnityEngine;
using UnityEngine.Assertions;

namespace HouraiTeahouse.Backroll {

public unsafe class InputQueue {

   int _id, _head, _tail, _length;
   bool _firstFrame;

   int _lastUserAddedFrame, _lastAddedFrame, _firstIncorrectFrame;
   int _lastFrameRequested;

   public int FrameDelay { get; set; }

   readonly GameInput[] _inputs;
   GameInput _prediction;

  int PreviousFrame(int offset) {
    return (offset == 0) ? (_inputs.Length - 1) : (offset - 1);
  }

  public InputQueue(int queue_size, uint input_size, int id = -1) {
     _id = id;
     _head = _tail = _length = FrameDelay = 0;
     _firstFrame = true;
     _lastUserAddedFrame = GameInput.kNullFrame;
     _firstIncorrectFrame = GameInput.kNullFrame;
     _lastFrameRequested = GameInput.kNullFrame;
     _lastAddedFrame = GameInput.kNullFrame;

     _prediction = new GameInput(GameInput.kNullFrame, null, input_size);

     // This is safe because we know the GameInput is a proper structure (as in,
     // no virtual methods, no contained classes, etc.).
     _inputs = new GameInput[queue_size];
     for (var i = 0; i < _inputs.Length; i++) {
        _inputs[i].Size = input_size;
     }
  }

  public int GetLastConfirmedFrame() {
     Debug.LogFormat("returning last confirmed frame {}.", _lastAddedFrame);
     return _lastAddedFrame;
  }

  public int GetFirstIncorrectFrame() {
     return _firstIncorrectFrame;
  }

  public void DiscardConfirmedFrames(int frame) {
     Assert.IsTrue(frame >= 0);

     if (_lastFrameRequested != GameInput.kNullFrame) {
        frame = Mathf.Min(frame, _lastFrameRequested);
     }

     Debug.LogFormat("discarding confirmed frames up to {} (last_added:{} length:{} [head:{} tail:{}]).",
         frame, _lastAddedFrame, _length, _head, _tail);
     if (frame >= _lastAddedFrame) {
        _tail = _head;
     } else {
        int offset = frame - _inputs[_tail].Frame + 1;

        Debug.LogFormat("difference of {} frames.", offset);
        Assert.IsTrue(offset >= 0);

        _tail = (_tail + offset) % _inputs.Length;
        _length -= offset;
     }

     Debug.LogFormat("after discarding, new tail is {} (frame:{}).", _tail, _inputs[_tail].Frame);
     Assert.IsTrue(_length >= 0);
  }

  public void ResetPrediction(int frame) {
     Assert.IsTrue(_firstIncorrectFrame == GameInput.kNullFrame || frame <= _firstIncorrectFrame);

     Debug.LogFormat("resetting all prediction errors back to frame {}.", frame);

     // There's nothing really to do other than reset our prediction
     // state and the incorrect frame counter...
     _prediction.Frame = GameInput.kNullFrame;
     _firstIncorrectFrame = GameInput.kNullFrame;
     _lastFrameRequested = GameInput.kNullFrame;
  }

  public bool GetConfirmedInput(int requested_frame, ref GameInput input) {
     Assert.IsTrue(_firstIncorrectFrame == GameInput.kNullFrame || requested_frame < _firstIncorrectFrame);
     int offset = requested_frame % _inputs.Length;
     if (_inputs[offset].Frame != requested_frame) return false;
     input = _inputs[offset];
     return true;
  }

  public bool GetInput(int requested_frame, out GameInput input) {
     Debug.LogFormat("requesting input frame {}.", requested_frame);

     // No one should ever try to grab any input when we have a prediction
     // error.  Doing so means that we're just going further down the wrong
     // path.  Assert.IsTrue this to verify that it's true.
     Assert.IsTrue(_firstIncorrectFrame == GameInput.kNullFrame);

     // Remember the last requested frame number for later.  We'll need
     // this in AddInput() to drop out of prediction mode.
     _lastFrameRequested = requested_frame;

     Assert.IsTrue(requested_frame >= _inputs[_tail].Frame);

     if (_prediction.Frame == GameInput.kNullFrame) {
        // If the frame requested is in our range, fetch it out of the queue and
        // return it.
        int offset = requested_frame - _inputs[_tail].Frame;

        if (offset < _length) {
           offset = (offset + _tail) % _inputs.Length;
           Assert.IsTrue(_inputs[offset].Frame == requested_frame);
           input = _inputs[offset];
           Debug.LogFormat("returning confirmed frame number {}.", input.Frame);
           return true;
        }

        // The requested frame isn't in the queue.  Bummer.  This means we need
        // to return a prediction frame.  Predict that the user will do the
        // same thing they did last time.
        if (requested_frame == 0) {
           Debug.Log("basing new prediction frame from nothing, you're client wants frame 0.");
           _prediction.Clear();
        } else if (_lastAddedFrame == GameInput.kNullFrame) {
           Debug.Log("basing new prediction frame from nothing, since we have no frames yet.");
           _prediction.Clear();
        } else {
           Debug.LogFormat("basing new prediction frame from previously added frame (queue entry:{}, frame:{}).",
                PreviousFrame(_head), _inputs[PreviousFrame(_head)].Frame);
           _prediction = _inputs[PreviousFrame(_head)];
        }
        _prediction.Frame++;
     }

     Assert.IsTrue(_prediction.Frame >= 0);

     // If we've made it this far, we must be predicting.  Go ahead and
     // forward the prediction frame contents.  Be sure to return the
     // frame number requested by the client, though.
     input = _prediction;
     input.Frame = requested_frame;
     Debug.LogFormat("returning prediction frame number {} ({}).", input.Frame, _prediction.Frame);

     return false;
  }

  public void AddInput(ref GameInput input) {
     int new_frame;

     Debug.LogFormat("adding input frame number {} to queue.", input.Frame);

     // These next two lines simply verify that inputs are passed in
     // sequentially by the user, regardless of frame delay.
     Assert.IsTrue(_lastUserAddedFrame == GameInput.kNullFrame ||
            input.Frame == _lastUserAddedFrame + 1);
     _lastUserAddedFrame = input.Frame;

     // Move the queue head to the correct point in preparation to
     // input the frame into the queue.
     new_frame = AdvanceQueueHead(input.Frame);
     if (new_frame != GameInput.kNullFrame) {
        AddDelayedInputToQueue(input, new_frame);
     }

     // Update the frame number for the input.  This will also set the
     // frame to GameInput.kNullFrame for frames that get dropped (by
     // design).
     input.Frame = new_frame;
  }

  protected void AddDelayedInputToQueue(in GameInput input, int frame_number) {
     Debug.LogFormat("adding delayed input frame number {} to queue.", frame_number);

     Assert.IsTrue(input.Size == _prediction.Size);
     Assert.IsTrue(_lastAddedFrame == GameInput.kNullFrame || frame_number == _lastAddedFrame + 1);
     Assert.IsTrue(frame_number == 0 || _inputs[PreviousFrame(_head)].Frame == frame_number - 1);

     // Add the frame to the back of the queue
     _inputs[_head] = input;
     _inputs[_head].Frame = frame_number;
     _head = (_head + 1) % _inputs.Length;
     _length++;
     _firstFrame = false;

     _lastAddedFrame = frame_number;

     if (_prediction.Frame != GameInput.kNullFrame) {
        Assert.IsTrue(frame_number == _prediction.Frame);

        // We've been predicting...  See if the inputs we've gotten match
        // what we've been predicting.  If so, don't worry about it.  If not,
        // remember the first input which was incorrect so we can report it
        // in GetFirstIncorrectFrame()
        if (_firstIncorrectFrame == GameInput.kNullFrame && !_prediction.Equals(input, true)) {
           Debug.LogFormat("frame {} does not match prediction.  marking error.", frame_number);
           _firstIncorrectFrame = frame_number;
        }

        // If this input is the same frame as the last one requested and we
        // still haven't found any mis-predicted inputs, we can dump out
        // of predition mode entirely!  Otherwise, advance the prediction frame
        // count up.
        if (_prediction.Frame == _lastFrameRequested && _firstIncorrectFrame == GameInput.kNullFrame) {
           Debug.Log("prediction is correct!  dumping out of prediction mode.");
           _prediction.Frame = GameInput.kNullFrame;
        } else {
           _prediction.Frame++;
        }
     }
     Assert.IsTrue(_length <= _inputs.Length);
  }

  protected int AdvanceQueueHead(int frame) {
     Debug.LogFormat("advancing queue head to frame {}.", frame);

     int expected_frame = _firstFrame ? 0 : _inputs[PreviousFrame(_head)].Frame + 1;

     frame += FrameDelay;

     if (expected_frame > frame) {
        // This can occur when the frame delay has dropped since the last
        // time we shoved a frame into the system.  In this case, there's
        // no room on the queue.  Toss it.
        Debug.LogFormat("Dropping input frame {} (expected next frame to be {}).",
            frame, expected_frame);
        return GameInput.kNullFrame;
     }

     while (expected_frame < frame) {
        // This can occur when the frame delay has been increased since the last
        // time we shoved a frame into the system.  We need to replicate the
        // last frame in the queue several times in order to fill the space
        // left.
        Debug.LogFormat("Adding padding frame {} to account for change in frame delay.",
            expected_frame);
        ref GameInput last_frame = ref _inputs[PreviousFrame(_head)];
        AddDelayedInputToQueue(last_frame, expected_frame);
        expected_frame++;
     }

     Assert.IsTrue(frame == 0 || frame == _inputs[PreviousFrame(_head)].Frame + 1);
     return frame;
  }

}


}
