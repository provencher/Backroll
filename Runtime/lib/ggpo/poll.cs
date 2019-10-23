using System.Collections.Generic;

namespace HouraiTeahouse.Backroll {

public class Poll {

  public delegate bool PollHandler(object obj);
  struct PollSink {
    public object      Cookie;
    public PollHandler Handler;
  }

  readonly List<PollSink> _loop_sinks;

  public Poll() {
    _loop_sinks = new List<PollSink>();
  }

  public void Run() {
     while (Pump()) continue;
  }

  public void RegisterLoop(PollHandler handler, object cookie = null) {
    _loop_sinks.Add(new PollSink {
        Handler = handler,
        Cookie = cookie
    });
  }

  public bool Pump() {
    bool finished = false;
    foreach (var sink in _loop_sinks) {
      finished = !sink.Handler(sink.Cookie) || finished;
    }
    return finished;
  }

}

}
