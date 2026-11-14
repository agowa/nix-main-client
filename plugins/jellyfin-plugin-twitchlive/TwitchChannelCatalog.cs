using System;
using System.Linq;

namespace Jellyfin.Plugin.TwitchLive;

/// <summary>
/// Names, limits and defaults shared by the tuner host and the guide provider.
/// </summary>
public static class TwitchChannelCatalog
{
    /// <summary>Tuner host id reported to Jellyfin; also the key listings providers enable per tuner.</summary>
    public const string HostId = "twitch";

    /// <summary>Prefix of every GraphQL alias, so that a login starting with a digit stays a valid name.</summary>
    internal const string AliasPrefix = "c_";

    /// <summary>
    /// How many channels one GraphQL document may ask for. Twitch rejects a query whose number of root
    /// field aliases exceeds 15 ("The number of root field aliases 50 exceeds the root field aliases
    /// limit allowed (15)"), so longer channel lists are split into documents - which still go out in a
    /// single HTTP request, because the endpoint accepts an array of queries.
    /// </summary>
    internal const int AliasesPerQuery = 15;

    /// <summary>Channel group for streamers that are live right now.</summary>
    public const string LiveGroup = "Twitch - Live";

    /// <summary>Channel group for streamers that are configured but not streaming.</summary>
    public const string OfflineGroup = "Twitch - Offline";

    /// <summary>The login as it is stored and compared: trimmed and lower-case.</summary>
    /// <param name="login">A Twitch login, in any casing.</param>
    /// <returns>The normalised login, or <c>null</c> if there was nothing to normalise.</returns>
    public static string? Normalize(string? login)
        => string.IsNullOrWhiteSpace(login) ? null : login.Trim().ToLowerInvariant();

    /// <summary>Turns a login into the GraphQL alias its answer arrives under.</summary>
    internal static string AliasFor(string normalizedLogin)
        => AliasPrefix + normalizedLogin;

    /// <summary>Reverses <see cref="AliasFor"/>.</summary>
    internal static string? LoginFromAlias(string alias)
        => alias.StartsWith(AliasPrefix, StringComparison.Ordinal) ? alias[AliasPrefix.Length..] : null;
}
