using System;

namespace Jellyfin.Plugin.ZdfLive;

/// <summary>
/// Thrown when an upstream host answered <c>429</c>, carrying how long it asked to be left alone.
/// </summary>
/// <remarks>
/// A separate exception type rather than a generic one because the caller's answer differs: every other failure
/// backs off by this plugin's own fixed delay, while a rate limit has been told exactly what delay is wanted and
/// asking again sooner is how one refusal turns into a block. The two plugins each carry their own copy on
/// purpose - they are built, versioned and installed independently (see <c>package.nix</c> in each folder), so a
/// shared assembly would be one more thing that has to move in step.
/// </remarks>
public sealed class UpstreamRateLimitedException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="UpstreamRateLimitedException"/> class.</summary>
    /// <param name="delay">How long to wait before asking again.</param>
    /// <param name="detail">What the host said, for the log line.</param>
    public UpstreamRateLimitedException(TimeSpan delay, string? detail)
        : base(string.IsNullOrEmpty(detail) ? "rate limited" : $"rate limited: {detail}")
        => Delay = delay;

    /// <summary>Gets how long the caller should wait before asking again.</summary>
    public TimeSpan Delay { get; }
}
