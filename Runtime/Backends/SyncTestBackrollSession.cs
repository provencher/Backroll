using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace HouraiTeahouse.Backroll {

public unsafe class SyncTestsBackrollSession<T> : BackrollSession<T> where T : struct {

   unsafe struct SavedInfo {
      public int         Frame;
      public int         Checksum;
      public byte*       Buffer;
      public int         Size;
      public GameInput   Input;
   };

    readonly BackrollSessionCallbacks _callbacks;
    readonly Sync                     _sync;
    int                    _num_players;
    int                    _check_distance;
    int                    _last_verified;
    bool                   _rollingback;
    bool                   _running;

    GameInput                  _current_input;
    GameInput                  _last_input;
    RingBuffer<SavedInfo>      _saved_frames;

    public SyncTestsBackrollSession(BackrollSessionConfig config, int frames) {
        _callbacks = config.Callbacks;
        _num_players = config.Players.Length;
        _check_distance = frames;
        _last_verified = 0;
        _rollingback = false;
        _running = false;
        _current_input.Clear();

        _sync = new Sync(null, new Sync.Config {
            NumPredictionFrames = BackrollConstants.kMaxPredictionFrames
        });
    }

    public override void Idle(int timeout) {
        if (_running) return;
        _callbacks.OnReady?.Invoke();
        _running = true;
    }

    public override void AddLocalInput(BackrollPlayerHandle player, ref T input) {}

    public override int SyncInput(void *values, int size) {
        if (_rollingback) {
            _last_input = _saved_frames.Peek().Input;
        } else {
            if (_sync.FrameCount == 0) {
                _sync.SaveCurrentFrame();
            }
            _last_input = _current_input;
        }
        fixed (byte* ptr = _last_input.bits) {
            UnsafeUtility.MemCpy(values, ptr, size);
        }
        return 0;
    }

    public override void AdvanceFrame() {
        _sync.IncrementFrame();
        _current_input.Clear();
        
        Debug.Log($"End of frame({_sync.FrameCount})...");

        if (_rollingback) return;

        int frame = _sync.FrameCount;
        // Hold onto the current frame in our queue of saved states.  We'll need
        // the Checksum later to verify that our replay of the same frame got the
        // same results.
        var info = new SavedInfo {
            Frame = frame,
            Input = _last_input,
            Size = _sync.GetLastSavedFrame().Size,
            Buffer = (byte*)UnsafeUtility.Malloc(_sync.GetLastSavedFrame().Size,
                                              UnsafeUtility.AlignOf<byte>(),
                                              Allocator.Temp),
            Checksum = _sync.GetLastSavedFrame().Checksum,
        };
        UnsafeUtility.MemCpy(info.Buffer, _sync.GetLastSavedFrame().Buffer, info.Size);
        _saved_frames.Push(info);

        if (frame - _last_verified == _check_distance) {
            // We've gone far enough ahead and should now start replaying frames.
            // Load the last verified frame and set the rollback flag to true.
            _sync.LoadFrame(_last_verified);

            _rollingback = true;
            while(!_saved_frames.IsEmpty) {
                _callbacks.AdvanceFrame();

                // Verify that the Checksumn of this frame is the same as the one in our
                // list.
                info = _saved_frames.Peek();
                _saved_frames.Pop();

                if (info.Frame != _sync.FrameCount) {
                    Debug.LogWarning($"SyncTest: Frame number {info.Frame} does not match saved frame number {frame}");
                }
                int Checksum = _sync.GetLastSavedFrame().Checksum;
                if (info.Checksum != Checksum) {
                    _callbacks.OnLogState?.Invoke($"Original f{_sync.FrameCount}:", (IntPtr)info.Buffer, info.Size);
                    _callbacks.OnLogState?.Invoke($"Replay   f{_sync.FrameCount}:", (IntPtr)_sync.GetLastSavedFrame().Buffer, 
                                                  _sync.GetLastSavedFrame().Size);
                    Debug.LogWarning($"SyncTest: Checksum for frame {frame} does not match saved ({Checksum} != {Checksum})");
                } else {
                    Debug.Log($"Checksum {Checksum} for frame {info.Frame} matches.");
                }
                UnsafeUtility.Free(info.Buffer, Allocator.Temp);
            }
            _last_verified = frame;
            _rollingback = false;
        }
    }

    public override void DisconnectPlayer(BackrollPlayerHandle player) =>
        throw new NotSupportedException();

    public override BackrollNetworkStats GetNetworkStats(BackrollPlayerHandle player) =>
        throw new NotSupportedException();

    public override void SetFrameDelay(BackrollPlayerHandle player, int Frame_delay) =>
        throw new NotSupportedException();

    public override void SetDisconnectNotifyStart(int timeout) =>
        throw new NotSupportedException();

    public override void SetDisconnectTimeout(int timeout) =>
        throw new NotSupportedException();

}

}