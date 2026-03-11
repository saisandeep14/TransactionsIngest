namespace TransactionsIngest.Configuration;

public sealed class AppSettings
{
    public const string SectionName = "AppSettings";

    public string ApiBaseUrl { get; init; } = "https://api.example.com";
    public string ApiPath { get; init; } = "/transactions/last24h";

    // if set, reads from this local file instead of calling the real API
    public string? MockFeedPath { get; init; }

    public int SnapshotWindowHours { get; init; } = 24;
}