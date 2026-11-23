using System;
using System.Collections.Generic;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TwitchLive.Configuration;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TwitchLive;

/// <summary>
/// Answers the two questions about the operator's Twitch account that the settings page used to ask by
/// itself: whose token is this, and whom does this account follow.
/// </summary>
/// <remarks>
/// Both were browser-side calls before - <c>id.twitch.tv/oauth2/validate</c> and
/// <c>/helix/channels/followed</c>, asked straight from dashboard JavaScript. That costs a long-lived
/// bearer credential: it travels in a cross-origin request started by whatever script and every browser
/// extension that document holds, and it is logged by any proxy in between. It also only works while
/// Twitch keeps answering cross-origin requests, so both buttons could have been failing quietly.
/// <para>
/// Asking this plugin instead means the stored token never leaves the process for either question: an
/// empty body means "the one you already have". The single exception is a token somebody has just typed
/// and not saved yet - that one arrives in the request body, from their own keystrokes, over the same
/// connection they save it on anyway. Nothing here accepts a URL.
/// </para>
/// <para>
/// Gated by <see cref="Policies.RequiresElevation"/>, which is what Jellyfin itself puts on
/// <c>PluginsController</c>: nobody can reach these who cannot already read the token back from
/// <c>GET /Plugins/{pluginId}/Configuration</c>.
/// </para>
/// </remarks>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
[Route("Plugins/" + TwitchAccountController.PluginRoute)]
public sealed class TwitchAccountController : ControllerBase
{
    /// <summary>
    /// Route segment this plugin owns, under the server's own plugin namespace. The id is
    /// <see cref="Plugin.PluginId"/> spelled out because an attribute needs a constant - meta.json carries
    /// the same one.
    /// </summary>
    public const string PluginRoute = "acfb8b6e-f2ce-4615-800f-77c40ffd213d";

    private readonly TwitchHelix _helix;

    /// <summary>Initializes a new instance of the <see cref="TwitchAccountController"/> class.</summary>
    /// <param name="helix">The account API this plugin already uses for follows and schedules.</param>
    public TwitchAccountController(TwitchHelix helix) => _helix = helix;

    /// <summary>Body of both requests. Every field is optional, including the whole body.</summary>
    public sealed class AccountRequest
    {
        /// <summary>A token to ask about instead of the stored one - only ever what somebody just typed.</summary>
        public string? Token { get; set; }

        /// <summary>How many followed logins to return at most; the configured limit applies when absent.</summary>
        public int? Limit { get; set; }
    }

    /// <summary>What Twitch says a token belongs to. Deliberately not the token itself.</summary>
    /// <param name="Login">The account's login name.</param>
    /// <param name="UserId">The account's numeric id.</param>
    /// <param name="Scopes">What it may read, so a missing scope can be named before anything needs it.</param>
    public sealed record TokenAnswer(string Login, string? UserId, IReadOnlyCollection<string> Scopes);

    /// <summary>The follow list of the account the token belongs to.</summary>
    /// <param name="Logins">Normalised logins, newest follow first.</param>
    /// <param name="Limit">The bound that was applied, so a short list can be told from a cut-off one.</param>
    public sealed record FollowAnswer(IReadOnlyList<string> Logins, int Limit);

    /// <summary>Why a request was refused, in the words Twitch or this plugin used.</summary>
    /// <param name="Error">One line an operator can act on.</param>
    public sealed record Refusal(string Error);

    /// <summary>
    /// Asks Twitch whose token is in use and what it may read.
    /// </summary>
    /// <param name="request">Optional body; send nothing to check the stored token.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The account's login, id and scopes, or a refusal.</returns>
    [HttpPost("ValidateToken")]
    [ProducesResponseType(typeof(TokenAnswer), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Refusal), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ValidateToken(
        [FromBody] AccountRequest? request,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

        if (!TryResolveToken(request, config, out var token, out var refusal))
        {
            return refusal!;
        }

        try
        {
            var (userId, login, scopes) = await _helix
                .ValidateTokenAsync(token!, config.ClientId, cancellationToken)
                .ConfigureAwait(false);

            return Ok(new TokenAnswer(login, userId, scopes));
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return Refuse(ex);
        }
    }

    /// <summary>
    /// Reads the follow list of whichever account the token belongs to.
    /// </summary>
    /// <param name="request">Optional body; send nothing to use the stored token and configured limit.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The followed logins, or a refusal naming what Twitch said.</returns>
    [HttpPost("FollowedChannels")]
    [ProducesResponseType(typeof(FollowAnswer), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Refusal), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> FollowedChannels(
        [FromBody] AccountRequest? request,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

        if (!TryResolveToken(request, config, out var token, out var refusal))
        {
            return refusal!;
        }

        // The page's own limit box, clamped to what Twitch and this plugin allow, or the setting it mirrors.
        var limit = request?.Limit is > 0 ? Math.Clamp(request.Limit.Value, 1, 2000) : config.MaxFollowedChannels;

        try
        {
            // refresh: true - a button press asks about now, and Twitch saying no has to reach the page
            // instead of being smoothed over with whatever the guide import cached last.
            var logins = await _helix
                .GetFollowedLoginsAsync(token!, config.ClientId, limit, cancellationToken, refresh: true)
                .ConfigureAwait(false);

            return Ok(new FollowAnswer(logins, limit));
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return Refuse(ex);
        }
    }

    /// <summary>Picks which token to ask about: what was typed, otherwise what is stored.</summary>
    private bool TryResolveToken(
        AccountRequest? request,
        PluginConfiguration config,
        out string? token,
        out IActionResult? refusal)
    {
        var typed = request?.Token?.Trim();

        // Only a value somebody actually typed crosses the wire; an empty field means "yours", so that
        // checking or importing never has to pull the stored credential back into the browser first.
        token = string.IsNullOrEmpty(typed) ? config.AuthToken?.Trim() : typed;

        if (string.IsNullOrEmpty(token))
        {
            refusal = BadRequest(new Refusal(
                "No token is stored and none was typed - everything except the follow list works anonymously."));
            return false;
        }

        refusal = null;
        return true;
    }

    /// <summary>Turns what went wrong into an answer the page can show, keeping a rate limit distinguishable.</summary>
    private ObjectResult Refuse(Exception ex)
    {
        // A 429 is a schedule rather than a mistake: the page should say how long to wait instead of
        // suggesting a fresh token, which is what every other failure here means.
        if (ex is UpstreamRateLimitedException limited)
        {
            return StatusCode(
                StatusCodes.Status429TooManyRequests,
                new Refusal($"Twitch is rate limiting this client id, come back in {(int)limited.Delay.TotalSeconds}s: {limited.Message}"));
        }

        // The messages from TwitchHelix are written for whoever typed the token; nothing here echoes it
        // back, and no address or header is added to them.
        return BadRequest(new Refusal(ex.Message));
    }
}
