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
public class SetPayload
{
    public string stage;
    public float? timer;
    public int? difficulty;
    public int? numplayers;
    public string lang;   // NEW
}

[Serializable]
public class InfPayload
{
    public string stage;
    public int? hits;
    public int? misses;
    public float? timeLeft;
    public int? score;    // NEW
}

