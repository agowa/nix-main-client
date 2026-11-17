using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.TwitchLive;

/// <summary>
/// One broadcast slot from a channel's published schedule.
/// </summary>
/// <param name="Id">Twitch's id for this slot, stable between refreshes while the slot exists.</param>
/// <param name="StartUtc">When the broadcast is scheduled to start.</param>
/// <param name="EndUtc">When it is scheduled to end; equal to the start when Twitch has no duration.</param>
/// <param name="Title">What the broadcaster called the slot. Often empty for a recurring weekly slot.</param>
/// <param name="Category">The category the broadcaster expects to stream, if they set one.</param>
/// <param name="IsRecurring">Whether this slot is part of a weekly series rather than a one-off.</param>
public sealed record TwitchScheduledItem(
    string Id,
    DateTime StartUtc,
    DateTime EndUtc,
    string Title,
    string? Category,
    bool IsRecurring);

/// <summary>
/// Reads the iCalendar feed Twitch serves for a channel's schedule.
/// </summary>
/// <remarks>
/// This is deliberately a hand-written reader rather than a dependency: the feed Twitch produces is a
/// narrow subset of RFC 5545 - one VEVENT per slot, no nested components, and recurring slots already
/// expanded into concrete dates for roughly the next week (each still carrying its RRULE, which we
/// ignore because the expansion is done for us). What it does need is the parts that are easy to get
/// wrong by splitting on colons: property parameters (<c>DTSTART;TZID=/America/Los_Angeles:...</c>),
/// folded lines (a continuation starts with a space), and escaped text (<c>\\n</c>, <c>\\,</c>).
/// <para>
/// Times arrive in the broadcaster's own zone with a TZID and no "Z", so they are converted with
/// <see cref="TimeZoneInfo"/> - .NET on Linux takes IANA names, which is what Twitch uses apart from a
/// leading slash it puts in front of them for reasons of its own.
/// </para>
/// </remarks>
public static class TwitchICalendar
{
    /// <summary>Parses a whole VCALENDAR document into its events, in the order they appear.</summary>
    public static List<TwitchScheduledItem> Parse(TextReader reader)
    {
        var items = new List<TwitchScheduledItem>();
        Dictionary<string, string>? current = null;
        string? previousName = null;

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            // RFC 5545 folds lines longer than 75 octets by continuing them with a space or tab.
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
            {
                if (previousName is not null && current is not null)
                {
                    current[previousName] += line[1..];
                }

                continue;
            }

            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                previousName = null;
                continue;
            }

            if (trimmed.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                previousName = null;
                continue;
            }

            if (trimmed.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                if (current is not null && TryBuild(current, out var item) && item is not null)
                {
                    items.Add(item);
                }

                current = null;
                previousName = null;
                continue;
            }

            if (current is null)
            {
                // Calendar-level properties (PRODID, X-WR-CALNAME, REFRESH-INTERVAL): not ours to use.
                continue;
            }

            var colon = trimmed.IndexOf(':');
            if (colon <= 0)
            {
                previousName = null;
                continue;
            }

            // "DTSTART;TZID=/Europe/Berlin" -> name "DTSTART", parameters kept with the value.
            var nameAndParams = trimmed[..colon];
            var value = trimmed[(colon + 1)..];
            var semicolon = nameAndParams.IndexOf(';');

            var name = semicolon == -1 ? nameAndParams : nameAndParams[..semicolon];

            if (semicolon != -1)
            {
                // The parameters have to travel with the value: DTSTART's TZID is part of it.
                value = nameAndParams[(semicolon + 1)..] + "\n" + value;
            }

            current[name] = value;
            previousName = name;
        }

        return items;
    }

    /// <summary>Parses a whole VCALENDAR document from its raw text.</summary>
    public static List<TwitchScheduledItem> Parse(string text)
    {
        using var reader = new StringReader(text);
        return Parse(reader);
    }

    private static bool TryBuild(Dictionary<string, string> values, out TwitchScheduledItem? item)
    {
        item = null;

        if (!values.TryGetValue("DTSTART", out var startRaw))
        {
            return false;
        }

        if (!TryReadDate(startRaw, out var start))
        {
            return false;
        }

        // A slot without an end time is not a lie worth making: one hour is what Twitch's own schedule
        // editor assumes when the broadcaster did not fill the duration in.
        var end = default(DateTime);
        if (!values.TryGetValue("DTEND", out var endRaw) || !TryReadDate(endRaw, out end))
        {
            end = start.AddHours(1);
        }

        if (end <= start)
        {
            return false;
        }

        var title = Unescape(values.GetValueOrDefault("SUMMARY"));
        var category = Unescape(values.GetValueOrDefault("CATEGORIES"));

        // DESCRIPTION is "Just Chatting." on a slot with no title, which reads better than "Scheduled".
        if (string.IsNullOrWhiteSpace(title))
        {
            var description = Unescape(values.GetValueOrDefault("DESCRIPTION")).TrimEnd('.', ' ');
            title = string.IsNullOrWhiteSpace(description) ? "Scheduled stream" : description;
        }

        var id = Unescape(values.GetValueOrDefault("UID"));
        if (string.IsNullOrWhiteSpace(id))
        {
            // Jellyfin keys programmes on the id, so it needs one even if Twitch stopped sending it.
            id = start.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
        }

        item = new TwitchScheduledItem(
            id,
            start,
            end,
            title,
            string.IsNullOrWhiteSpace(category) ? null : category,
            values.ContainsKey("RRULE"));

        return true;
    }

    /// <summary>
    /// Reads one date-time property, honouring its TZID parameter and converting to UTC.
    /// </summary>
    private static bool TryReadDate(string raw, out DateTime utc)
    {
        utc = default;

        string zoneId = string.Empty;
        var value = raw;

        // Parameters were parked on the first line when a property carried them (see Parse).
        var newline = raw.IndexOf('\n');
        if (newline != -1)
        {
            var parameters = raw[..newline];
            value = raw[(newline + 1)..].Trim();

            foreach (var part in parameters.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');
                if (eq != -1 && part[..eq].Equals("TZID", StringComparison.OrdinalIgnoreCase))
                {
                    zoneId = part[(eq + 1)..];
                }
            }
        }

        value = value.Trim();

        // DATE values (all-day) have no time part; RFC 5545 also allows basic and extended forms, but
        // Twitch only ever writes YYYYMMDDTHHMMSS with an optional Z.
        if (value.EndsWith("Z", StringComparison.Ordinal))
        {
            if (!DateTime.TryParseExact(
                    value,
                    new[] { "yyyyMMdd'T'HHmmss'Z'", "yyyyMMdd'T'HHmm'Z'", "yyyy-MM-ddTHH:mm:ssZ" },
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out utc))
            {
                return false;
            }

            return true;
        }

        if (!DateTime.TryParseExact(
                value,
                new[] { "yyyyMMdd'T'HHmmss", "yyyyMMdd'T'HHmm", "yyyy-MM-ddTHH:mm:ss", "yyyyMMdd" },
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var local))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(zoneId))
        {
            // No zone at all: the feed's CALSCALE is Gregorian and Twitch's fallback is UTC.
            utc = DateTime.SpecifyKind(local, DateTimeKind.Utc);
            return true;
        }

        try
        {
            // Twitch writes "/America/Los_Angeles"; .NET wants "America/Los_Angeles".
            var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId.TrimStart('/'));
            utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone);
            return true;
        }
        catch (Exception)
        {
            // An unknown zone is a reason to be wrong about the hour, not to drop the appointment.
            utc = DateTime.SpecifyKind(local, DateTimeKind.Utc);
            return true;
        }
    }

    /// <summary>Undoes RFC 5545 text escaping.</summary>
    private static string Unescape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i == value.Length - 1)
            {
                sb.Append(value[i]);
                continue;
            }

            i++;
            switch (value[i])
            {
                case 'n':
                case 'N':
                    sb.Append('\n');
                    break;
                case ',':
                case ';':
                case ':':
                case '\\':
                    sb.Append(value[i]);
                    break;
                default:
                    sb.Append('\\').Append(value[i]);
                    break;
            }
        }

        return sb.ToString().Trim();
    }
}
