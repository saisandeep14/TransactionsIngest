namespace TransactionsIngest.Configuration;

public sealed class AppSettings
{
    public const string SectionName = "AppSettings";

    /// <summary>Base URL of the transactions API.</summary>
    public string ApiBaseUrl { get; init; } = "https://api.example.com";

    /// <summary>Path appended to ApiBaseUrl when fetching transactions.</summary>
    public string ApiPath { get; init; } = "/transactions/last24h";

    /// <summary>
    /// When set, the application reads from this local JSON file instead of
    /// calling the real API. Useful for local testing and development.
    /// </summary>
    public string? MockFeedPath { get; init; }

    /// <summary>
    /// How many hours back the snapshot window covers. Defaults to 24.
    /// Records older than this are candidates for finalization.
    /// </summary>
    public int SnapshotWindowHours { get; init; } = 24;
}
