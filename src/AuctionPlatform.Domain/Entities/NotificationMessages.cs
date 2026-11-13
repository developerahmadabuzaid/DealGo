using AuctionPlatform.Domain.Common;

namespace AuctionPlatform.Domain.Entities;

/// <summary>
/// Staff's own wording for one of the messages the system sends by itself (an auction starting, a
/// prize won, an order shipped), or that message switched off. A text left empty uses the
/// built-in one. One row per message, created the first time staff change it.
/// </summary>
public class NotificationMessageSetting : AuditableEntity
{
    /// <summary>Which automatic message, e.g. "auction_started".</summary>
    public string Code { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public string? TitleAr { get; set; }
    public string? BodyAr { get; set; }
    public string? TitleEn { get; set; }
    public string? BodyEn { get; set; }

    public Guid? UpdatedByUserId { get; set; }
}

public enum ScheduledNotificationAudience
{
    /// <summary>Every active account with notifications on.</summary>
    Everyone = 0,

    /// <summary>Only the people who joined the linked auction.</summary>
    AuctionParticipants = 1
}

public enum ScheduledNotificationStatus
{
    Scheduled = 0,
    Sending = 1,
    Sent = 2,
    Cancelled = 3,
    Failed = 4
}

/// <summary>A message staff wrote in the dashboard to go out at a time they chose.</summary>
public class ScheduledNotification : AuditableEntity
{
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;

    /// <summary>Tapping the notification opens this auction; with participants as the audience, whose.</summary>
    public Guid? AuctionId { get; set; }

    public ScheduledNotificationAudience Audience { get; set; }

    public DateTime SendAt { get; set; }
    public ScheduledNotificationStatus Status { get; set; } = ScheduledNotificationStatus.Scheduled;

    public DateTime? SentAt { get; set; }

    /// <summary>How many accounts it was written for.</summary>
    public int? Recipients { get; set; }

    public string? Error { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public uint Version { get; set; }
}
