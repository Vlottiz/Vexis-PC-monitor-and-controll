// The shape of every sendToHost({...}) message from the pages. One class for all of them:
// each message type only fills the fields it needs and the rest stay null.
public class IncomingMessage
{
    public string?                     type     { get; set; }
    public string?                     file     { get; set; }
    public string?                     page     { get; set; }
    public string?                     url      { get; set; }
    public Dictionary<string, string>? colors   { get; set; }
    public Dictionary<string, string>? settings { get; set; }
    public string? method  { get; set; }
    public string? path    { get; set; }
    public string? body    { get; set; }
    public int?    port    { get; set; }
    public string? callId  { get; set; }
    public int?                        slot     { get; set; }
    public string?                     fanName  { get; set; }
    public float?                      value    { get; set; }
    public bool?                       enabled  { get; set; }
    public FanCurve?                   fanCurve { get; set; }
    public int?  seq      { get; set; }
    public int?  delta    { get; set; } // zoom: +1 / -1 / 0 (reset)
    public RecordOptions? record { get; set; } // recordStart: what to record
    public int?  pid      { get; set; } // endTask: one process (otherwise every process named "file")
    public long? started  { get; set; } // endTask: that process's start time as the page showed it
    public int?  presetIdx { get; set; } // saveColors: which theme preset was picked
    public string? profile { get; set; } // setFanProfile: custom | silent | balanced | performance
    public DupRequest? dup { get; set; } // duplicate finder: scan options / files to recycle
}

public class DupRequest
{
    public List<string>? paths { get; set; }          // folders / drives to scan
    public double        minMb { get; set; } = 1;     // ignore smaller files
    public bool          includeSystem { get; set; }  // also scan Windows / Program Files / AppData
    public List<string>? files { get; set; }          // dupRecycle: files to send to the Recycle Bin
}
