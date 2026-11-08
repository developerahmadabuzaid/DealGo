namespace AuctionPlatform.Application.Notifications;

/// <summary>
/// Tells staff that the SMS provider has no balance left. Customers only ever read "temporarily
/// unavailable", so without this the first anyone hears of it is a complaint.
/// </summary>
public interface ISmsBalanceAlert
{
    /// <summary>
    /// Records the refusal and notifies the admins, at most once every few hours. Never throws: it
    /// runs while a customer is waiting for their own error message.
    /// </summary>
    Task RaiseAsync(CancellationToken ct = default);

    /// <summary>When the balance was first found empty, or null once a code has gone out since.</summary>
    Task<DateTime?> GetEmptySinceAsync(CancellationToken ct = default);
}
