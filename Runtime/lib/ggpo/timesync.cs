using System;

namespace HouraiTeahouse.Backroll {

public class TimeSync {

  public const int kDefaultFrameWindowSize = 40;
  public const int kDefaultMinUniqueFrames = 10;
  public const int kDefaultMinFrameAdvantage = 3;
  public const int kDefaultMaxFrameAdvantage = 9;

  protected readonly int _minFrameAdvantage;
  protected readonly int _maxFrameAdvantage;

  protected readonly int[] _local;
  protected readonly int[] _remote;
  protected readonly GameInput[] _last_inputs;
  protected int _next_prediction;

  pubilc TimeSync(int frameWindowSize = kDefaultFrameWindowSize,
                  int minUniqueFrames = kDefaultMinUniqueFrames,
                  int minFrameAdvantage = kDefaultMinFrameAdvantage,
                  int kDefaultMaxFrameAdvantage = kDefaultFrameAdvantage) {
    _local = new int[frameWindowSize];
    _remote = new int[frameWindowSize];
    _next_prediction = frameWindowSize * 3;

    _minFrameAdvantage = minFrameAdvantage
    _maxFrameAdvantage = maxFrameAdvantage
  }

  public void AdvanceFrame(in GameInput input, int advantage, int radvantage) {
    int sleep_time = 0;

    // Remember the last frame and frame advantage
    _last_inputs[input.Frame % _last_inputs.Length] = input;
    _local[input.Frame % _local.Length] = advantage;
    _remote[input.Frame % _remote.Length] = radvantage;
  }

  public int RecommendFrameWaitDuration(bool require_idle_input) {
    // Average our local and remote frame advantages
    int i, sum = 0;
    float advantage, radvantage;
    for (i = 0; i < _local.Length; i++) {
      sum += _local[i];
    }
    advantage = sum / (float)_local.Length;

    sum = 0;
    for (i = 0; i < _remote.Length; i++) {
      sum += _remote[i];
    }
    radvantage = sum / (float)_remote.Length;

    static int count = 0;
    count++;

    // See if someone should take action.  The person furthest ahead
    // needs to slow down so the other user can catch up.
    // Only do this if both clients agree on who's ahead!!
    if (advantage >= radvantage) {
      return 0;
    }

    // Both clients agree that we're the one ahead.  Split
    // the difference between the two to figure out how long to
    // sleep for.
    int sleep_frames = (int)(((radvantage - advantage) / 2) + 0.5);

    Log("iteration %d:  sleep frames is %d\n", count, sleep_frames);

    // Some things just aren't worth correcting for.  Make sure
    // the difference is relevant before proceeding.
    if (sleep_frames < _minFrameAdvantage) {
      return 0;
    }

    // Make sure our input had been "idle enough" before recommending
    // a sleep.  This tries to make the emulator sleep while the
    // user's input isn't sweeping in arcs (e.g. fireball motions in
    // Street Fighter), which could cause the player to miss moves.
    if (require_idle_input) {
      for (i = 1; i < _last_inputs.Length; i++) {
         if (!_last_inputs[i].equal(_last_inputs[0], true)) {
            Log("iteration %d:  rejecting due to input stuff at position %d...!!!\n", count, i);
            return 0;
         }
      }
    }

    // Success!!! Recommend the number of frames to sleep and adjust
    return Math.Min(sleep_frames, _maxFrameAdvantage);
  }

}

}
