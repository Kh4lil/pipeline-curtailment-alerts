namespace PipelineAlerts;

internal sealed record Notice(
    string Pipeline,
    string Type,
    string Title,
    DateTimeOffset PostedAt,
    string? Location,
    Uri Link,
    string Body);

internal sealed record TradingSignal(Notice Notice, string Keyword, string Area, string? Volume);
