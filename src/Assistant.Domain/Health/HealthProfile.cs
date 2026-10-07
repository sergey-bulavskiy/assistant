namespace Assistant.Domain.Health;

/// <summary>The one tracked person of a `health` role bot (exactly one row per bot). Family-scoped.</summary>
public class HealthProfile
{
    public const string DefaultSubjectTag = "health";
    public const string DefaultTimeZone = "UTC";
    public const string DefaultEmergencyPhone = "103 или 112";

    public long Id { get; set; }
    public long FamilyId { get; set; }

    /// <summary>bots.id of the health bot (unique: one profile per bot).</summary>
    public long BotId { get; set; }

    public string SubjectTag { get; set; } = DefaultSubjectTag;

    /// <summary>Day 0 of the stage week count; null until an owner sets it (/setstart).</summary>
    public DateOnly? StageStartDate { get; set; }

    /// <summary>IANA time zone id; "today" and local times are computed in it.</summary>
    public string TimeZone { get; set; } = DefaultTimeZone;

    /// <summary>Emergency number text shown in alerts (/setphone).</summary>
    public string EmergencyPhone { get; set; } = DefaultEmergencyPhone;

    /// <summary>Owner-written context, at most 500 characters (/setnote); consultation only.</summary>
    public string? ContextNote { get; set; }

    public string? Conditions { get; set; }
    public string? Medications { get; set; }
    public string? Allergies { get; set; }
    public string? DoctorPlan { get; set; }
    public string? DoctorContacts { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Telegram user id of the owner who last changed the profile.</summary>
    public long? UpdatedByUserId { get; set; }
}
