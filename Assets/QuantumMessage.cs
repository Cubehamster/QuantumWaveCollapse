using System;

[Serializable]
public class QuantumMessage
{
    public string sndr;   // sender id, e.g. "squid-ctrl"
    public string con;    // "eth", "wifi", "disconnected"
    public string ip;
    public int? rssi;
    public string vers;

    public InfPayload inf;   // puzzle -> server
    public SetPayload set;   // server -> puzzle

    public string trig;      // "usr", "time", "server", "logic"
}

[Serializable]
public class InfPayload
{
    public string stage;     // "idle", "intro", "playing", "solved", ...

    // Squid-specific extras (pick what you need):
    public int? hits;
    public int? misses;
    public float? timeLeft;
}

[Serializable]
public class SetPayload
{
    public string stage;     // server sets puzzle stage
    public float? timer;     // time allowed
    public int? difficulty;  // optional

    // Game-wide variable from server:
    public int? numplayers;
}
