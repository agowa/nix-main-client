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
    UserAgent: ""
};

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

export default class TwitchLiveConfigPage {
    constructor(view) {
        this.view = view;

        this.form = view.querySelector("#twitchLiveForm");
        this.channels = view.querySelector("#txtChannels");
        this.includeOffline = view.querySelector("#chkIncludeOffline");
        this.allowSource = view.querySelector("#chkAllowSource");
        this.statusCache = view.querySelector("#numStatusCache");
        this.tokenCache = view.querySelector("#numTokenCache");
        this.guideAhead = view.querySelector("#numGuideAhead");
        this.clientId = view.querySelector("#txtClientId");
        this.userAgent = view.querySelector("#txtUserAgent");

        // The form is inside a page that stays in the DOM after navigating away, so bind once here
        // rather than on every show.
        this.form.addEventListener("submit", (event) => this.submit(event));

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
        this.statusCache.value = config.StatusCacheSeconds;
        this.tokenCache.value = config.TokenCacheSeconds;
        this.guideAhead.value = config.GuideAheadMinutes;
        this.clientId.value = config.ClientId || "";
        this.userAgent.value = config.UserAgent || "";
    }

    async submit(event) {
        event.preventDefault();

        // One login per line, but accept commas too - copying a list out of a chat or a spreadsheet
        // tends to produce them.
        const channels = (this.channels.value || "")
            .split(/[\s,]+/)
            .map((login) => login.trim().toLowerCase())
            .filter((login) => login.length > 0);

        if (!channels.length && !window.confirm("No channels left - the tuner will offer nothing. Save anyway?")) {
            return;
        }

        const updated = Object.assign({}, this.config || FALLBACK, {
            Channels: channels,
            IncludeOfflineChannels: this.includeOffline.checked,
            AllowSourceQuality: this.allowSource.checked,
            StatusCacheSeconds: Math.max(5, parseInt(this.statusCache.value, 10) || 60),
            TokenCacheSeconds: Math.max(0, parseInt(this.tokenCache.value, 10) || 0),
            GuideAheadMinutes: Math.max(15, parseInt(this.guideAhead.value, 10) || 240),
            ClientId: (this.clientId.value || "").trim() || FALLBACK.ClientId,
            UserAgent: (this.userAgent.value || "").trim()
        });

        try {
            const response = await fetch(configurationUrl(), {
                method: "PUT",
                headers: headers(true),
                body: JSON.stringify(updated)
            });

            if (!response.ok) {
                throw new Error("HTTP " + response.status);
            }

            this.config = updated;
            this.channels.value = channels.join("\n");

            // The same toast the rest of the dashboard uses, when it is reachable from here.
            if (window.Dashboard && typeof Dashboard.processPluginConfigurationUpdateResult === "function") {
                Dashboard.processPluginConfigurationUpdateResult(response);
            } else {
                window.alert("Saved.");
            }
        } catch (error) {
            console.error("[Twitch Live TV] could not save the configuration", error);

            if (window.Dashboard && typeof Dashboard.processErrorResponse === "function") {
                Dashboard.processErrorResponse(error);
            } else {
                window.alert("Could not save: " + error.message);
            }
        }
    }
}
