namespace AuctionPlatform.Application.M5azn;

/// <summary>What started an import.</summary>
public enum M5aznImportTrigger
{
    Schedule = 0,
    Button = 1
}

/// <summary>The steps of an import, in order.</summary>
public enum M5aznImportPhase
{
    Starting = 0,
    ReadingList = 1,
    ReadingDetails = 2,
    Categories = 3,
    Saving = 4,
    Finished = 5,
    Failed = 6
}

/// <summary>
/// How the import is going, for the progress bar on the M5azn page. It is kept on the server, so
/// the bar is right however the import was started and after the page is closed and opened again.
/// </summary>
public sealed record M5aznImportStatus(
    bool Running,
    M5aznImportTrigger Trigger,
    M5aznImportPhase Phase,

    /// <summary>0 to 100; null while there is nothing to measure against (the very first import's list).</summary>
    int? Percent,

    /// <summary>Products found in M5azn's list so far.</summary>
    int Found,

    /// <summary>Progress inside the current step, when the step can be counted.</summary>
    int StepDone,
    int StepTotal,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    M5aznImportSummary? Summary,
    string? Error)
{
    /// <summary>Filled in when the status is read.</summary>
    public int ElapsedSeconds { get; init; }

    /// <summary>True on the answer to a request for an import while one was already going.</summary>
    public bool AlreadyRunning { get; init; }

    public static M5aznImportStatus Idle { get; } =
        new(false, M5aznImportTrigger.Schedule, M5aznImportPhase.Finished, null, 0, 0, 0, null, null, null, null);
}

/// <summary>
/// Hands an import asked for in the dashboard to a background worker. The import takes minutes and
/// must go on when the admin closes the page, so it cannot live inside their request.
/// </summary>
public interface IM5aznImportQueue
{
    /// <summary>False when an import is already waiting to start.</summary>
    bool TryRequest();
}
