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

/** Twitch's own endpoints. Both answer cross-origin on purpose (they serve browser apps). */
const TWITCH_VALIDATE_URL = "https://id.twitch.tv/oauth2/validate";
const TWITCH_FOLLOWED_URL = "https://api.twitch.tv/helix/channels/followed";

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
        this.authToken.value = config.AuthToken || "";
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
            AuthToken: (this.authToken.value || "").trim(),
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

    /** Asks Twitch whose token is in the field, without saving anything first. */
    async checkToken() {
        const token = (this.authToken.value || "").trim();
        if (!token) {
            this.status("No token to check - the plugin works anonymously without one.");
            return;
        }

        this.status("Checking with Twitch...");

        try {
            const info = await this.validateToken(token);
            const scopes = (info.scopes && info.scopes.length) ? info.scopes.join(", ") : "(none)";
            let note = "";

            if (!info.scopes || info.scopes.indexOf("user:read:follows") === -1) {
                note = " Without user:read:follows the follow list stays unavailable.";
            }

            this.status("Token is valid for '" + (info.login || "?") + "' (user id " + (info.user_id || "?")
                + "), scopes: " + scopes + "." + note);
        } catch (error) {
            this.status("Twitch refused this token: " + error.message, true);
        }
    }

    /** Pulls the follow list of the token's account into the channel box. */
    async addFollows() {
        const token = (this.authToken.value || "").trim();
        if (!token) {
            this.status("Add my follows needs an OAuth token above - Twitch only gives the list to the account that owns it.", true);
            return;
        }

        this.status("Reading your follow list from Twitch...");

        try {
            const info = await this.validateToken(token);
            const clientId = (this.clientId.value || "").trim() || FALLBACK.ClientId;
            const limit = Math.min(2000, Math.max(1, parseInt(this.followLimit.value, 10) || 200));

            const existing = (this.channels.value || "")
                .split(/[\s,]+/)
                .map((login) => login.trim().toLowerCase())
                .filter((login) => login.length > 0);

            let cursor = null;
            let added = 0;
            let pages = 0;

            do {
                const params = new URLSearchParams({ user_id: info.user_id, first: "100" });
                if (cursor) {
                    params.set("after", cursor);
                }

                const response = await fetch(TWITCH_FOLLOWED_URL + "?" + params.toString(), {
                    headers: { Authorization: "Bearer " + token, "Client-Id": clientId }
                });

                if (!response.ok) {
                    this.status("Twitch refused the follow list (HTTP " + response.status + ")"
                        + (response.status === 403 ? " - the token needs the scope user:read:follows" : ""), true);
                    return;
                }

                const body = await response.json();
                (body.data || []).forEach((entry) => {
                    const login = (entry.broadcaster_login || "").toLowerCase();
                    if (login && existing.indexOf(login) === -1 && added < limit) {
                        existing.push(login);
                        added++;
                    }
                });

                cursor = (body.pagination && body.pagination.cursor) || null;
                pages++;
            } while (cursor && added < limit && pages < 20);

            this.channels.value = existing.join("\n");
            this.status(added ? "Added " + added + " followed channel(s) to the box - press Save to keep them."
                : "Nothing to add: every channel you follow is already in the list.");
        } catch (error) {
            this.status("Could not read your follow list: " + error.message, true);
        }
    }

    /**
     * Calls Twitch's own token validation endpoint from the browser.
     *
     * It is used rather than a round trip through the plugin because there is nothing to add for it:
     * Twitch answers cross-origin requests on both id.twitch.tv and api.twitch.tv, so the page can ask
     * directly and report exactly what Twitch said about the token that was just typed in.
     */
    async validateToken(token) {
        const clientId = (this.clientId.value || "").trim() || FALLBACK.ClientId;
        const response = await fetch(TWITCH_VALIDATE_URL, {
            headers: { Authorization: "OAuth " + token, "Client-Id": clientId }
        });

        if (!response.ok) {
            throw new Error("HTTP " + response.status + " - issue a fresh one at twitchtokengenerator.com or dev.twitch.tv");
        }

        return response.json();
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
