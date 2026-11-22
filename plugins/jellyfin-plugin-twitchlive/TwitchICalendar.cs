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
    /// <summary>Most events taken from one feed. A channel publishes a few dozen; this is a ceiling, not an expectation.</summary>
    public const int MaxEvents = 5000;

    /// <summary>Longest single property value accepted after unfolding (a description of a broadcast is a paragraph).</summary>
    private const int MaxValueLength = 64 * 1024;

    /// <summary>Most lines walked, so a feed that never ends cannot hold a guide refresh open.</summary>
    private const int MaxLines = 500_000;

    /// <summary>Parses a whole VCALENDAR document into its events, in the order they appear.</summary>
    /// <param name="reader">The feed.</param>
    /// <returns>The events found.</returns>
    public static List<TwitchScheduledItem> Parse(TextReader reader)
        => Parse(reader, out _);

    /// <summary>
    /// Parses a whole VCALENDAR document into its events, saying whether it stopped early.
    /// </summary>
    /// <remarks>
    /// The feed is Twitch's and the shape it writes is narrow, but it is still somebody else's answer parsed
    /// into a guide: three bounds keep an unexpected - or hostile - document from deciding how much of this
    /// process gets used. Values are accumulated in a builder rather than re-joined per continuation line,
    /// because folding turns one long description into hundreds of lines and <c>value += more</c> copies the
    /// whole value every time.
    /// </remarks>
    /// <param name="reader">The feed.</param>
    /// <param name="truncated">Set when a bound was hit, so the caller can say so instead of showing fewer slots quietly.</param>
    /// <returns>The events found.</returns>
    public static List<TwitchScheduledItem> Parse(TextReader reader, out bool truncated)
    {
        var items = new List<TwitchScheduledItem>();
        Dictionary<string, StringBuilder>? current = null;
        StringBuilder? folded = null;
        string? previousName = null;
        var lines = 0;
        truncated = false;

        // A folded property is only finished once something other than a continuation line follows it.
        void Flush()
        {
            if (previousName is not null && folded is not null && current is not null)
            {
                current[previousName] = folded;
            }

            folded = null;
            previousName = null;
        }

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (++lines > MaxLines)
            {
                truncated = true;
                break;
            }

            // RFC 5545 folds lines longer than 75 octets by continuing them with a space or tab.
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
            {
                if (previousName is not null && folded is not null)
                {
                    if (folded.Length <= MaxValueLength)
                    {
                        folded.Append(line, 1, line.Length - 1);
                    }
                    else
                    {
                        // Keep the event, drop the rest of that one value: a slot with a truncated description
                        // is still an appointment, and this says the feed was not read to the end.
                        truncated = true;
                    }
                }

                continue;
            }

            Flush();

            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                current = new Dictionary<string, StringBuilder>(StringComparer.OrdinalIgnoreCase);
                continue;
            }

            if (trimmed.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                if (current is not null && TryBuild(current, out var item) && item is not null)
                {
                    if (items.Count >= MaxEvents)
                    {
                        truncated = true;
                        current = null;
                        break;
                    }

                    items.Add(item);
                }

                current = null;
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

            current[name] = new StringBuilder(value);
            previousName = name;
            folded = current[name];
        }

        Flush();

        // An event still open when the document ended is not an event: without END:VEVENT there is no telling
        // whether what followed was truncated by the transport or never written.
        return items;
    }

    /// <summary>Parses a whole VCALENDAR document from its raw text.</summary>
    /// <param name="text">The feed.</param>
    /// <returns>The events found.</returns>
    public static List<TwitchScheduledItem> Parse(string text)
        => Parse(text, out _);

    /// <summary>Parses a whole VCALENDAR document from its raw text, saying whether it stopped early.</summary>
    /// <param name="text">The feed.</param>
    /// <param name="truncated">Set when a bound was hit.</param>
    /// <returns>The events found.</returns>
    public static List<TwitchScheduledItem> Parse(string text, out bool truncated)
    {
        using var reader = new StringReader(text);
        return Parse(reader, out truncated);
    }

    /// <summary>One property's accumulated text, or <c>null</c> when the event did not carry it.</summary>
    private static string? Raw(Dictionary<string, StringBuilder> values, string name)
        => values.TryGetValue(name, out var value) ? value.ToString() : null;

    private static bool TryBuild(Dictionary<string, StringBuilder> values, out TwitchScheduledItem? item)
    {
        item = null;

        var startRaw = Raw(values, "DTSTART");
        if (startRaw is null)
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
        var endRaw = Raw(values, "DTEND");
        if (endRaw is null || !TryReadDate(endRaw, out end))
        {
            end = start.AddHours(1);
        }

        if (end <= start)
        {
            return false;
        }

        var title = Unescape(Raw(values, "SUMMARY"));
        var category = Unescape(Raw(values, "CATEGORIES"));

        // DESCRIPTION is "Just Chatting." on a slot with no title, which reads better than "Scheduled".
        if (string.IsNullOrWhiteSpace(title))
        {
            var description = Unescape(Raw(values, "DESCRIPTION")).TrimEnd('.', ' ');
            title = string.IsNullOrWhiteSpace(description) ? "Scheduled stream" : description;
        }

        var id = Unescape(Raw(values, "UID"));
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
