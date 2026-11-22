namespace LogTracer;

public sealed record EventRecord(
    DateTime Timestamp,
    string LogName,
    string Level,
    int EventId,
    string Source,
    string Message)
{
    public string Summary => Message.ReplaceLineEndings(" ");
}
