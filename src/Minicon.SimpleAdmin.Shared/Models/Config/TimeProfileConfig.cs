namespace Minicon.SimpleAdmin.Models.Config;

/// <summary>
/// Time profiles configuration for threshold adjustments based on time of day/week
/// </summary>
public class TimeProfilesConfig
{
    /// <summary>
    /// Business hours profile
    /// </summary>
    public TimeProfile? BusinessHours { get; set; }

    /// <summary>
    /// After hours profile
    /// </summary>
    public TimeProfile? AfterHours { get; set; }

    /// <summary>
    /// Weekend profile
    /// </summary>
    public TimeProfile? Weekend { get; set; }

    /// <summary>
    /// Custom profiles (key = profile name)
    /// </summary>
    public Dictionary<string, TimeProfile> Custom { get; set; } = new();

    /// <summary>
    /// Gets the currently active profile based on current time
    /// </summary>
    /// <returns>The active profile or null if no profile matches</returns>
    public TimeProfile? GetActiveProfile()
    {
        return GetActiveProfile(DateTime.Now);
    }

    /// <summary>
    /// Gets the active profile for a specific time
    /// </summary>
    /// <param name="dateTime">The time to check</param>
    /// <returns>The active profile or null if no profile matches</returns>
    public TimeProfile? GetActiveProfile(DateTime dateTime)
    {
        // Check weekend first
        if (Weekend != null && Weekend.IsActiveAt(dateTime))
        {
            return Weekend;
        }

        // Check after hours
        if (AfterHours != null && AfterHours.IsActiveAt(dateTime))
        {
            return AfterHours;
        }

        // Check business hours
        if (BusinessHours != null && BusinessHours.IsActiveAt(dateTime))
        {
            return BusinessHours;
        }

        // Check custom profiles
        foreach (var profile in Custom.Values)
        {
            if (profile.IsActiveAt(dateTime))
            {
                return profile;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the threshold multiplier for the current time
    /// </summary>
    /// <returns>The multiplier (1.0 if no profile active)</returns>
    public double GetCurrentMultiplier()
    {
        return GetActiveProfile()?.ThresholdMultiplier ?? 1.0;
    }
}

/// <summary>
/// A time profile that defines when it's active and what multiplier to apply
/// </summary>
public class TimeProfile
{
    /// <summary>
    /// Schedule definition (e.g., "Mon-Fri 07:00-18:00", "Sat 00:00 - Mon 07:00")
    /// </summary>
    public string Schedule { get; set; } = string.Empty;

    /// <summary>
    /// Threshold multiplier when this profile is active (e.g., 1.0 for normal, 1.5 for relaxed)
    /// </summary>
    public double ThresholdMultiplier { get; set; } = 1.0;

    /// <summary>
    /// Optional: Description of this profile
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Checks if this profile is active at the specified time
    /// </summary>
    /// <param name="dateTime">The time to check</param>
    /// <returns>True if active</returns>
    public bool IsActiveAt(DateTime dateTime)
    {
        return ScheduleParser.IsInSchedule(Schedule, dateTime);
    }
}

/// <summary>
/// Helper class for parsing schedule strings
/// </summary>
internal static class ScheduleParser
{
    private static readonly Dictionary<string, DayOfWeek> DayMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Mon"] = DayOfWeek.Monday,
        ["Tue"] = DayOfWeek.Tuesday,
        ["Wed"] = DayOfWeek.Wednesday,
        ["Thu"] = DayOfWeek.Thursday,
        ["Fri"] = DayOfWeek.Friday,
        ["Sat"] = DayOfWeek.Saturday,
        ["Sun"] = DayOfWeek.Sunday,
        ["Monday"] = DayOfWeek.Monday,
        ["Tuesday"] = DayOfWeek.Tuesday,
        ["Wednesday"] = DayOfWeek.Wednesday,
        ["Thursday"] = DayOfWeek.Thursday,
        ["Friday"] = DayOfWeek.Friday,
        ["Saturday"] = DayOfWeek.Saturday,
        ["Sunday"] = DayOfWeek.Sunday
    };

    /// <summary>
    /// Checks if a datetime falls within a schedule
    /// </summary>
    /// <param name="schedule">Schedule string (e.g., "Mon-Fri 07:00-18:00")</param>
    /// <param name="dateTime">DateTime to check</param>
    /// <returns>True if in schedule</returns>
    public static bool IsInSchedule(string schedule, DateTime dateTime)
    {
        if (string.IsNullOrWhiteSpace(schedule))
        {
            return false;
        }

        // Simple format: "Mon-Fri 07:00-18:00"
        var parts = schedule.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        var dayPart = parts[0];
        var timePart = parts[1];

        // Parse days (e.g., "Mon-Fri" or "Sat")
        if (!IsInDayRange(dayPart, dateTime.DayOfWeek))
        {
            return false;
        }

        // Parse time (e.g., "07:00-18:00")
        if (!IsInTimeRange(timePart, dateTime.TimeOfDay))
        {
            return false;
        }

        return true;
    }

    private static bool IsInDayRange(string dayPart, DayOfWeek dayOfWeek)
    {
        if (dayPart.Contains('-'))
        {
            var days = dayPart.Split('-');
            if (days.Length == 2 && DayMap.TryGetValue(days[0], out var startDay) && DayMap.TryGetValue(days[1], out var endDay))
            {
                // Handle wrap-around (e.g., Fri-Mon)
                if (startDay <= endDay)
                {
                    return dayOfWeek >= startDay && dayOfWeek <= endDay;
                }
                else
                {
                    return dayOfWeek >= startDay || dayOfWeek <= endDay;
                }
            }
        }
        else if (DayMap.TryGetValue(dayPart, out var singleDay))
        {
            return dayOfWeek == singleDay;
        }

        return false;
    }

    private static bool IsInTimeRange(string timePart, TimeSpan timeOfDay)
    {
        if (timePart.Contains('-'))
        {
            var times = timePart.Split('-');
            if (times.Length == 2)
            {
                if (TimeSpan.TryParse(times[0], out var startTime) && TimeSpan.TryParse(times[1], out var endTime))
                {
                    // Handle wrap-around (e.g., 22:00-06:00)
                    if (startTime <= endTime)
                    {
                        return timeOfDay >= startTime && timeOfDay < endTime;
                    }
                    else
                    {
                        return timeOfDay >= startTime || timeOfDay < endTime;
                    }
                }
            }
        }

        return false;
    }
}
