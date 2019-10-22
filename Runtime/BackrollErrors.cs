using System;

namespace HouraiTeahouse.Backroll {

public class BackrollException : Exception {

  readonly BackrollErrorCode ErrorCode;

  public BackrollException(BackrollErrorCode errorCode) : base($"Backroll Error: {errorCode}") {
    ErrorCode = errorCode;
  }

}

public enum BackrollErrorCode : sbyte {
  OK                  = 0,
  Success             = 0,
  GeneralFailure      = -1,
  InvalidSession      = 1,
  InvalidPlayerHandle = 2,
  PlayerOutOfRange    = 3,
  PredictionThreshold = 4,
  Unsupported         = 5,
  NotSynchronized     = 6,
  InRollback          = 7,
  InputDropped        = 8,
  PlayerDisconnected  = 9,
  TooManySpectators   = 10,
  InvalidRequest      = 11
}

}
