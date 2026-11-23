/**
 * Controller for the Twitch Live TV configuration page.
 *
 * Jellyfin 10.11 no longer runs script embedded in a plugin's HTML page: the view container looks for a
 * `data-controller` attribute on the page element and imports the module registered under that name,
 * then constructs it with the page element. Hence this file is a separate embedded resource and the only
 * place where the page's behaviour lives.
 */

const PLUGIN_ID = "acfb8b6e-f2ce-4615-800f-77c40ffd213d";

/** Values as the plugin declares them, used when the server has never been configured. */
const FALLBACK = {
    Channels: ["lofigirl"],
    IncludeOfflineChannels: true,
    ClientId: "kimne78kx3ncx6brgo4mv6wki5h1ko",
    StatusCacheSeconds: 60,
    TokenCacheSeconds: 120,
    GuideAheadMinutes: 240,
    AllowSourceQuality: true,
    UserAgent: "",
    AuthToken: "",
    UseTokenForPlayback: true,
    ImportFollowedChannels: false,
    MaxFollowedChannels: 200,
    FollowCacheMinutes: 360,
    UseTwitchSchedule: true,
    ScheduleAheadDays: 7,
    ShowChannelLogos: true
};

/**
 * The plugin's own endpoints for the two account questions.
 *
 * They exist so that this page never talks to Twitch: asking id.twitch.tv or api.twitch.tv from here put a
 * long-lived bearer credential into a cross-origin request started by dashboard code, visible to every
 * extension and script in this document - and it only worked at all while Twitch kept answering
 * cross-origin. See TwitchAccountController for the server side of this.
 */
function accountUrl(action) {
    return ApiClient.getUrl("Plugins/" + PLUGIN_ID + "/" + action);
}

/**
 * Asks the plugin to do something with the account token and returns its answer.
 *
 * The body carries a token only when somebody typed one; an empty body means "use what you have", which is
 * how checking or importing stops needing the stored credential in the browser at all.
 */
async function askAccount(action, body) {
    const response = await fetch(accountUrl(action), {
        method: "POST",
        headers: headers(true),
        body: JSON.stringify(body || {})
    });

    if (!response.ok) {
        let detail = "";
        try {
            detail = (await response.json()).Error || "";
        } catch (ignored) {
            // A refusal we could not read is still a refusal; the status line below says that much.
        }

        throw new Error(detail || ("the plugin answered HTTP " + response.status));
    }

    return response.json();
}

function headers(withBody) {
    const h = { "X-Emby-Token": ApiClient.accessToken() };
    if (withBody) {
        h["Content-Type"] = "application/json";
    }
    return h;
}

function configurationUrl() {
    return ApiClient.getUrl("Plugins/" + PLUGIN_ID + "/Configuration");
}

/** Turns anything thrown or returned into one line a human can act on. */
async function describeFailure(error, response) {
    if (response) {
        let detail = "";
        try {
            const text = await response.text();
            detail = text ? " - " + text.substring(0, 200) : "";
        } catch (ignored) {
            // A body we could not read is not worth losing the status code over.
        }

        return "HTTP " + response.status + " " + (response.statusText || "") + detail;
    }

    if (!error) {
        return "unknown error";
    }

    return error.message || String(error);
}

export default class TwitchLiveConfigPage {
    constructor(view) {
        this.view = view;

        this.form = view.querySelector("#twitchLiveForm");
        this.channels = view.querySelector("#txtChannels");
        this.includeOffline = view.querySelector("#chkIncludeOffline");
        this.allowSource = view.querySelector("#chkAllowSource");
        this.showLogos = view.querySelector("#chkShowLogos");
        this.useSchedule = view.querySelector("#chkUseSchedule");
        this.scheduleDays = view.querySelector("#numScheduleDays");
        this.statusCache = view.querySelector("#numStatusCache");
        this.tokenCache = view.querySelector("#numTokenCache");
        this.guideAhead = view.querySelector("#numGuideAhead");
        this.clientId = view.querySelector("#txtClientId");
        this.userAgent = view.querySelector("#txtUserAgent");
        this.authToken = view.querySelector("#txtAuthToken");
        this.useTokenForPlayback = view.querySelector("#chkUseTokenForPlayback");
        this.importFollows = view.querySelector("#chkImportFollows");
        this.followLimit = view.querySelector("#numFollowLimit");
        this.tokenStatus = view.querySelector("#tokenStatus");
        this.revealToken = view.querySelector("#btnRevealToken");

        // The stored token lives here rather than in the input - see configPage.html for why the field starts
        // empty. Saving writes this back unless the operator typed something new, so opening the page can neither
        // lose a working token nor display one.
        this.storedToken = "";
        this.tokenEdited = false;

        this.authToken.addEventListener("input", () => { this.tokenEdited = true; });
        if (this.revealToken) {
            this.revealToken.addEventListener("click", () => this.reveal());
        }

        // The form is inside a page that stays in the DOM after navigating away, so bind once here
        // rather than on every show.
        this.form.addEventListener("submit", (event) => this.submit(event));
        view.querySelector("#btnCheckToken").addEventListener("click", () => this.checkToken());
        view.querySelector("#btnLoadFollows").addEventListener("click", () => this.addFollows());

        this.load();
    }

    async load() {
        let config = FALLBACK;

        try {
            const response = await fetch(configurationUrl(), { headers: headers(false) });
            if (response.ok) {
                // Keep whatever the server has for fields this page does not show, so saving cannot
                // drop a setting added by a later version.
                config = Object.assign({}, FALLBACK, await response.json());
            } else {
                console.error("[Twitch Live TV] configuration request failed: " + response.status);
            }
        } catch (error) {
            console.error("[Twitch Live TV] could not read the configuration", error);
        }

        this.config = config;

        const list = Array.isArray(config.Channels) ? config.Channels : [];
        this.channels.value = list.join("\n");
        this.includeOffline.checked = config.IncludeOfflineChannels !== false;
        this.allowSource.checked = config.AllowSourceQuality !== false;
        this.showLogos.checked = config.ShowChannelLogos !== false;
        this.useSchedule.checked = config.UseTwitchSchedule !== false;
        this.useTokenForPlayback.checked = config.UseTokenForPlayback !== false;
        this.importFollows.checked = config.ImportFollowedChannels === true;
        this.scheduleDays.value = config.ScheduleAheadDays;
        this.followLimit.value = config.MaxFollowedChannels;
        this.statusCache.value = config.StatusCacheSeconds;
        this.tokenCache.value = config.TokenCacheSeconds;
        this.guideAhead.value = config.GuideAheadMinutes;
        this.clientId.value = config.ClientId || "";
        this.userAgent.value = config.UserAgent || "";

        // Deliberately not rendered into the field. It stays in memory so that saving keeps it and so "Check
        // token" can still ask Twitch about it - which is what lets this page work without ever displaying a live
        // credential to whatever else can see this document.
        this.storedToken = config.AuthToken || "";
        this.tokenEdited = false;
        this.authToken.value = "";
        this.authToken.placeholder = this.storedToken
            ? "(stored - press Show to see it, or type to replace it)"
            : "(none - everything works anonymously)";
    }

    /** Puts the stored token into the field, on request only. */
    reveal() {
        if (!this.storedToken) {
            this.status("Nothing is stored yet - paste a token to add one.");
            return;
        }

        this.authToken.type = "text";
        this.authToken.value = this.storedToken;

        // From here the field is the truth, so an edit made after revealing is what gets saved.
        this.tokenEdited = true;
        if (this.revealToken) {
            this.revealToken.style.display = "none";
        }
    }

    /** Reads the form into the shape the plugin's configuration class expects. */
    collect() {
        // One login per line, but accept commas too - copying a list out of a chat or a spreadsheet
        // tends to produce them. Follows are not stored here on purpose: they come from Twitch at
        // runtime, so pasting them in is only ever a convenience (see addFollows).
        const channels = (this.channels.value || "")
            .split(/[\s,]+/)
            .map((login) => login.trim().toLowerCase())
            .filter((login) => login.length > 0);

        return Object.assign({}, this.config || FALLBACK, {
            Channels: channels,
            IncludeOfflineChannels: this.includeOffline.checked,
            AllowSourceQuality: this.allowSource.checked,
            ShowChannelLogos: this.showLogos.checked,
            UseTwitchSchedule: this.useSchedule.checked,
            ScheduleAheadDays: Math.min(28, Math.max(1, parseInt(this.scheduleDays.value, 10) || 7)),
            MaxFollowedChannels: Math.min(2000, Math.max(1, parseInt(this.followLimit.value, 10) || 200)),
            StatusCacheSeconds: Math.max(5, parseInt(this.statusCache.value, 10) || 60),
            TokenCacheSeconds: Math.max(0, parseInt(this.tokenCache.value, 10) || 0),
            GuideAheadMinutes: Math.max(15, parseInt(this.guideAhead.value, 10) || 240),
            ClientId: (this.clientId.value || "").trim() || FALLBACK.ClientId,
            UserAgent: (this.userAgent.value || "").trim(),

            // An untouched field means "keep what is stored": it is empty on purpose, so reading it literally
            // would clear a working token the first time an unrelated setting was saved.
            AuthToken: this.tokenEdited ? (this.authToken.value || "").trim() : this.storedToken,
            UseTokenForPlayback: this.useTokenForPlayback.checked,
            ImportFollowedChannels: this.importFollows.checked
        });
    }

    async submit(event) {
        event.preventDefault();

        const updated = this.collect();

        if (!updated.Channels.length && !updated.ImportFollowedChannels
            && !window.confirm("No channels left and no follow import - the tuner will offer nothing. Save anyway?")) {
            return;
        }

        try {
            // POST, not PUT: Jellyfin's PluginsController only offers [HttpPost("{pluginId}/Configuration")]
            // (10.11 as far back as it exists), and a PUT is answered 405 - which used to surface here as
            // the dashboard's error dialog reading .status off an Error object, i.e. "undefined".
            const response = await fetch(configurationUrl(), {
                method: "POST",
                headers: headers(true),
                body: JSON.stringify(updated)
            });

            if (!response.ok) {
                this.status("Could not save: " + await describeFailure(null, response), true);
                return;
            }

            this.config = updated;
            this.storedToken = updated.AuthToken;
            this.channels.value = updated.Channels.join("\n");

            // The same toast the rest of the dashboard uses when it is reachable from here; the inline
            // line underneath always says something, so the page never depends on dashboard internals.
            if (window.Dashboard && typeof window.Dashboard.processPluginConfigurationUpdateResult === "function") {
                window.Dashboard.processPluginConfigurationUpdateResult();
            }

            // Saving queues the rebuild server-side (see GuideRefresh.cs), so there is nothing left to run here.
            this.status("Saved - the channel list is being rebuilt now.");
        } catch (error) {
            console.error("[Twitch Live TV] could not save the configuration", error);
            this.status("Could not save: " + await describeFailure(error), true);
        }
    }

    /** The typed token if there is one - otherwise nothing, and the server uses what it holds. */
    typedToken() {
        return this.tokenEdited ? (this.authToken.value || "").trim() : "";
    }

    /** Asks the plugin whose token is in use, without saving anything first. */
    async checkToken() {
        const typed = this.typedToken();
        if (!typed && !this.storedToken) {
            this.status("No token to check - the plugin works anonymously without one.");
            return;
        }

        this.status(typed ? "Asking Twitch about the token you typed..." : "Asking Twitch about the stored token...");

        try {
            const answer = await askAccount("ValidateToken", typed ? { Token: typed } : {});
            const scopes = (answer.Scopes && answer.Scopes.length) ? answer.Scopes.join(", ") : "(none)";
            let note = "";

            if (!answer.Scopes || answer.Scopes.indexOf("user:read:follows") === -1) {
                note = " Without user:read:follows the follow list stays unavailable.";
            }

            this.status("Token is valid for '" + (answer.Login || "?") + "' (user id " + (answer.UserId || "?")
                + "), scopes: " + scopes + "." + note);
        } catch (error) {
            this.status("Twitch refused this token: " + error.message, true);
        }
    }

    /** Asks the plugin for the follow list and puts it in the channel box. */
    async addFollows() {
        const typed = this.typedToken();
        if (!typed && !this.storedToken) {
            this.status("Add my follows needs an OAuth token above - Twitch only gives the list to the account that owns it.", true);
            return;
        }

        this.status("Reading the follow list from Twitch...");

        try {
            const limit = Math.min(2000, Math.max(1, parseInt(this.followLimit.value, 10) || 200));
            const answer = await askAccount("FollowedChannels", typed ? { Token: typed, Limit: limit } : { Limit: limit });

            const existing = (this.channels.value || "")
                .split(/[\s,]+/)
                .map((login) => login.trim().toLowerCase())
                .filter((login) => login.length > 0);

            let added = 0;
            (answer.Logins || []).forEach((login) => {
                if (login && existing.indexOf(login) === -1 && added < limit) {
                    existing.push(login);
                    added++;
                }
            });

            this.channels.value = existing.join("\n");
            this.status(added ? "Added " + added + " followed channel(s) to the box - press Save to keep them."
                : "Nothing to add: every channel in that list is already here.");
        } catch (error) {
            this.status("Could not read your follow list: " + error.message, true);
        }
    }

    /** Shows a line inside the page. The only status mechanism this page depends on. */
    status(text, isError) {
        if (!this.tokenStatus) {
            return;
        }

        this.tokenStatus.style.display = "block";
        this.tokenStatus.textContent = text;
        this.tokenStatus.style.color = isError ? "#e05959" : "";
    }
}
