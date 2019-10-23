using UnityEngine;

namespace HouraiTeahouse.Backroll {

public static class BackrollTime {

    public static uint GetTime() {
        return (uint)Mathf.FloorToInt(Time.realtimeSinceStartup * 1000);
    }

}

}