/**
 * Controller for the ZDF Live TV configuration page.
 *
 * Jellyfin 10.11 does not run script embedded in a plugin's HTML page: the view container reads the
 * page's `data-controller` attribute, imports the module registered under that name and constructs it
 * with the page element. Hence this file is a separate embedded resource.
 */

const PLUGIN_ID = "f0a3242b-6358-494a-ad7a-8d5c50c90a20";

/** The plugin's own catalogue, served as another of its embedded pages (see Plugin.GetPages). */
const CATALOG_PAGE = "zdfLiveCatalog";

/** Where the channel list comes from when it is asked for live. CORS is allowed on this host. */
const STREAM_API = "https://api.zapp.mediathekview.de/v1/channelInfoList";

const FALLBACK = {
    EnabledChannels: [],
    CacheMinutes: 60,
    ApiBaseUrl: STREAM_API,
    UserAgent: "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36",
    SendRefererHeader: true,
    ShowChannelLogos: true,
    LogoTemplate: "https://static.epg.best/de/{id}.png",
    LogoUrls: []
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

async function describeFailure(error, response) {
    if (response) {
        let detail = "";
        try {
            const text = await response.text();
            detail = text ? " - " + text.substring(0, 200) : "";
        } catch (ignored) {
            // A body that cannot be read is not worth losing the status code over.
        }

        return "HTTP " + response.status + " " + (response.statusText || "") + detail;
    }

    if (!error) {
        return "unknown error";
    }

    return error.message || String(error);
}

export default class ZdfLiveConfigPage {
    constructor(view) {
        this.view = view;
        this.form = view.querySelector("#zdfLiveForm");
        this.list = view.querySelector("#channelList");
        this.filter = view.querySelector("#txtFilter");
        this.cacheMinutes = view.querySelector("#numCacheMinutes");
        this.apiBaseUrl = view.querySelector("#txtApiBaseUrl");
        this.userAgent = view.querySelector("#txtUserAgent");
        this.referer = view.querySelector("#chkReferer");
        this.showLogos = view.querySelector("#chkShowLogos");
        this.logoTemplate = view.querySelector("#txtLogoTemplate");
        this.logoOverrides = view.querySelector("#txtLogoOverrides");
        this.statusLine = null;

        // key -> {key, name, epgId, group, default, catalogued}
        this.channels = [];
        this.enabled = new Set();

        this.form.addEventListener("submit", (event) => this.submit(event));
        this.filter.addEventListener("input", () => this.render());
        view.querySelector("#btnSelectDefaults").addEventListener("click", () => this.select(c => c.default));
        view.querySelector("#btnSelectZdf").addEventListener("click", () => this.select(c => c.group === "ZDF Live"));
        view.querySelector("#btnSelectAll").addEventListener("click", () => this.select(() => true));
        view.querySelector("#btnSelectNone").addEventListener("click", () => this.select(() => false));

        this.load();
    }

    async load() {
        let config = FALLBACK;

        try {
            const response = await fetch(configurationUrl(), { headers: headers(false) });
            if (response.ok) {
                // Keep fields this page does not show, so saving cannot drop a setting from a later version.
                config = Object.assign({}, FALLBACK, await response.json());
            } else {
                this.status("Could not read the settings: HTTP " + response.status, true);
            }
        } catch (error) {
            console.error("[ZDF Live TV] could not read the configuration", error);
            this.status("Could not read the settings: " + await describeFailure(error), true);
        }

        this.config = config;

        // Tick exactly what is configured. Only a configuration without the field at all falls back to
        // the plugin's defaults; an empty list means nothing is offered, which is why saving one asks
        // first (see ZdfChannelRepository.RequestedKeys).
        const configured = Array.isArray(config.EnabledChannels) ? config.EnabledChannels.filter(Boolean) : [];
        this.enabled = new Set(configured.map(k => String(k).toLowerCase()));

        this.cacheMinutes.value = config.CacheMinutes;
        this.apiBaseUrl.value = config.ApiBaseUrl || "";
        this.userAgent.value = config.UserAgent || "";
        this.referer.checked = config.SendRefererHeader !== false;
        this.showLogos.checked = config.ShowChannelLogos !== false;
        this.logoTemplate.value = config.LogoTemplate || "";

        this.logoOverrides.value = (Array.isArray(config.LogoUrls) ? config.LogoUrls : [])
            .filter(p => p && p.Name && p.Value)
            .map(p => p.Name + "=" + p.Value)
            .join("\n");

        await this.loadChannels();
    }

    /**
     * Builds the channel list: the plugin's catalogue first (those entries carry a guide id and a logo),
     * then anything else the stream API is currently serving.
     */
    async loadChannels() {
        const byKey = new Map();

        try {
            const response = await fetch(ApiClient.getUrl("web/ConfigurationPage", { name: CATALOG_PAGE }), { headers: headers(false) });
            if (response.ok) {
                const catalog = await response.json();
                (catalog.channels || []).forEach(c => byKey.set(c.key.toLowerCase(), Object.assign({ catalogued: true }, c)));
            }
        } catch (error) {
            console.error("[ZDF Live TV] could not read the catalogue", error);
        }

        if (!byKey.size) {
            // The catalogue is an embedded resource, so this only fails on a broken installation. The
            // API list below still makes the page usable.
            this.status("The plugin's channel catalogue could not be loaded; showing what the stream API reports.", true);
        }

        try {
            const response = await fetch(this.apiBaseUrl.value || STREAM_API);
            if (response.ok) {
                const api = await response.json();
                Object.keys(api || {}).forEach(key => {
                    const known = byKey.get(key.toLowerCase());
                    if (known) {
                        // The API's own names are unreliable ("NRD Niedersachsen"), so the catalogue wins.
                        return;
                    }

                    byKey.set(key.toLowerCase(), {
                        key: key,
                        name: (api[key] && api[key].name) || key,
                        epgId: "",
                        group: "ARD/ZDF Live",
                        default: false,
                        catalogued: false
                    });
                });
            } else {
                this.status("The stream API answered HTTP " + response.status + "; only the built-in catalogue is listed.", true);
            }
        } catch (error) {
            // Offline or blocked: the catalogue alone still covers every channel that works offline too.
            console.warn("[ZDF Live TV] stream API unreachable", error);
        }

        this.channels = Array.from(byKey.values());

        // Anything enabled but unknown (a key typed over the REST API, a channel the broadcasters
        // dropped) has to stay visible or saving would silently delete it.
        const missing = Array.from(this.enabled).filter(k => !byKey.has(k));
        missing.forEach(key => this.channels.push({ key: key, name: key + " (not offered by the stream API right now)", epgId: "", group: "?", default: false, catalogued: false }));

        this.render();
    }

    render() {
        const needle = (this.filter.value || "").trim().toLowerCase();
        const rows = this.channels.filter(c => !needle
            || (c.name || "").toLowerCase().indexOf(needle) !== -1
            || c.key.toLowerCase().indexOf(needle) !== -1);

        if (!rows.length) {
            this.list.innerHTML = '<div class="fieldDescription">Nothing matches that filter.</div>';
            return;
        }

        const esc = (s) => String(s == null ? "" : s).replace(/[&<>"]/g, ch => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[ch]));

        this.list.innerHTML = rows.map(c => {
            const checked = this.enabled.has(c.key.toLowerCase()) ? " checked" : "";
            const guide = c.epgId ? esc(c.epgId) : '<em>matched by name</em>';
            return '<div class="listItem" style="padding:.4em 0;">'
                + '<label class="checkboxContainer listItemCheckboxContainer">'
                + '<input type="checkbox" is="emby-checkbox" class="chkChannel" data-key="' + esc(c.key) + '"' + checked + ' /><span></span>'
                + '</label><div class="listItemBody two-line">'
                + '<div class="listItemBodyName">' + esc(c.name) + (c.default ? ' <span class="fieldDescription">(default)</span>' : "") + "</div>"
                + '<div class="listItemBodyText">' + esc(c.key) + " &middot; " + esc(c.group) + " &middot; guide id: " + guide + "</div>"
                + "</div></div>";
        }).join("");

        this.list.querySelectorAll(".chkChannel").forEach(box => {
            box.addEventListener("change", () => {
                const key = box.getAttribute("data-key").toLowerCase();
                if (box.checked) {
                    this.enabled.add(key);
                } else {
                    this.enabled.delete(key);
                }
            });
        });
    }

    /** Applies a predicate to the rows currently visible, so the filter still works as a selection tool. */
    select(predicate) {
        const needle = (this.filter.value || "").trim().toLowerCase();
        this.channels.forEach(c => {
            const visible = !needle
                || (c.name || "").toLowerCase().indexOf(needle) !== -1
                || c.key.toLowerCase().indexOf(needle) !== -1;

            if (!visible) {
                return;
            }

            const key = c.key.toLowerCase();
            if (predicate(c)) {
                this.enabled.add(key);
            } else {
                this.enabled.delete(key);
            }
        });

        this.render();
    }

    collect() {
        // Order matters: it becomes the channel numbers in the guide. Keep the catalogue's order and
        // append anything else that is ticked, so ticking a new channel never renumbers the old ones.
        const ordered = [];
        this.channels.forEach(c => {
            if (this.enabled.has(c.key.toLowerCase())) {
                ordered.push(c.key);
            }
        });

        const logoUrls = (this.logoOverrides.value || "")
            .split(/[\r\n,]+/)
            .map(line => line.trim())
            .filter(line => line.indexOf("=") > 0)
            .map(line => ({ Name: line.substring(0, line.indexOf("=")).trim(), Value: line.substring(line.indexOf("=") + 1).trim() }));

        return Object.assign({}, this.config || FALLBACK, {
            EnabledChannels: ordered,
            CacheMinutes: Math.min(1440, Math.max(1, parseInt(this.cacheMinutes.value, 10) || 60)),
            ApiBaseUrl: (this.apiBaseUrl.value || "").trim() || FALLBACK.ApiBaseUrl,
            UserAgent: (this.userAgent.value || "").trim(),
            SendRefererHeader: this.referer.checked,
            ShowChannelLogos: this.showLogos.checked,
            LogoTemplate: (this.logoTemplate.value || "").trim(),
            LogoUrls: logoUrls
        });
    }

    async submit(event) {
        event.preventDefault();

        const updated = this.collect();

        if (!updated.EnabledChannels.length
            && !window.confirm("No channels selected - the tuner will offer nothing. Save anyway?")) {
            return;
        }

        try {
            // POST: Jellyfin's PluginsController only offers [HttpPost("{pluginId}/Configuration")].
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

            if (window.Dashboard && typeof window.Dashboard.processPluginConfigurationUpdateResult === "function") {
                window.Dashboard.processPluginConfigurationUpdateResult();
            }

            // Saving queues the rebuild server-side (see GuideRefresh.cs), so there is nothing left to run here.
            this.status("Saved - the channel list is being rebuilt now.");
        } catch (error) {
            console.error("[ZDF Live TV] could not save the configuration", error);
            this.status("Could not save: " + await describeFailure(error), true);
        }
    }

    /** Shows a line inside the page - the only status mechanism this page depends on. */
    status(text, isError) {
        if (!this.statusLine) {
            this.statusLine = document.createElement("div");
            this.statusLine.className = "fieldDescription";
            this.statusLine.style.marginTop = ".8em";
            this.form.appendChild(this.statusLine);
        }

        this.statusLine.textContent = text;
        this.statusLine.style.color = isError ? "#e05959" : "";
    }
}
