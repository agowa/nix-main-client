using System;
using System.Collections.Generic;
using System.IO;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ZdfLive;

/// <summary>
/// The channel logos that come inside the plugin's own assembly, as files on disk.
/// </summary>
/// <remarks>
/// Artwork that has to be fetched is artwork that can fail to arrive, and for a Live TV channel that
/// failure is not merely a missing picture: Jellyfin downloads a channel's remote logo lazily - the first
/// time anything asks for the tile (<c>ImageController.GetImage()</c>) - and when that download fails it
/// answers the request with an error *and* deletes the image reference it was holding, because
/// <c>LibraryManager.ConvertImageToLocal()</c> is called there with <c>removeOnFailure: true</c>. The tile
/// then stays broken until some other code writes artwork again, which for a channel means waiting for
/// <c>GuideManager.UpdateChannel()</c>, and that only touches the image of a channel that has none. So one
/// hiccup at the logo host costs a row of unusable tiles for an unpredictable time.
/// <para>
/// The channels no logo host carries therefore bring their picture along in the assembly (see
/// <c>Assets/SOURCES.md</c>) and this class turns those resources into files, so what the server is offered
/// is a path it can read straight away - nothing to download, nothing to time out. Channels that do use the
/// configured logo host keep doing that; see <see cref="ZdfChannelCatalog.LogoFor"/> for the order.
/// </para>
/// <para>
/// They are unpacked into the server's data directory rather than next to the DLL because the plugin folder
/// is replaced on every update - and under NixOS it is not even writable between them - while a channel's
/// stored image path outlives updates and has to keep resolving. Writing is limited to files that are
/// missing or of a different size, so this costs nothing after the first run.
/// </para>
/// </remarks>
public sealed class ChannelLogoAssets
{
    /// <summary>Folder under the server's data directory the logos are unpacked into.</summary>
    private static readonly string[] FolderParts = ["ZdfLive", "logos"];

    /// <summary>Suffix of the resources this class is interested in; also what the files are called.</summary>
    private const string Extension = ".png";

    /// <summary>What separates the folder part of an embedded resource's name from the file name.</summary>
    private const string AssetMarker = ".Assets.";

    private readonly ILogger<ChannelLogoAssets> _logger;
    private readonly object _gate = new();

    /// <summary>File name to absolute path once unpacked; empty when unpacking is not possible at all.</summary>
    private Dictionary<string, string>? _unpacked;

    /// <summary>Initializes a new instance of the <see cref="ChannelLogoAssets"/> class.</summary>
    /// <param name="applicationPaths">The server's well known paths.</param>
    /// <param name="logger">The logger.</param>
    public ChannelLogoAssets(IApplicationPaths applicationPaths, ILogger<ChannelLogoAssets> logger)
    {
        Folder = Path.Combine(applicationPaths.DataPath, Path.Combine(FolderParts));
        _logger = logger;
    }

    /// <summary>Gets the directory the bundled logos live in.</summary>
    public string Folder { get; }

    /// <summary>
    /// Gets the file this plugin ships under a given name.
    /// </summary>
    /// <remarks>
    /// Unpacking is deferred to the first call rather than done in the constructor: this runs while the
    /// server is still building its services, and a plugin should not be creating directories at that point
    /// - especially not when Live TV is installed but switched off again before anyone looks at it. The
    /// blocking file access is deliberate: six small files, once per server run, on a path the server owns.
    /// </remarks>
    /// <param name="fileName">File name as named in <c>Assets/</c>, e.g. <c>logo_3sat.png</c>.</param>
    /// <returns>The absolute path, or <c>null</c> when no such logo is shipped or it could not be written.</returns>
    public string? PathFor(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var unpacked = EnsureUnpacked();

        return unpacked.TryGetValue(fileName, out var path) ? path : null;
    }

    /// <summary>Unpacks the embedded logos once, and remembers what where ended up where.</summary>
    private Dictionary<string, string> EnsureUnpacked()
    {
        lock (_gate)
        {
            if (_unpacked is not null)
            {
                return _unpacked;
            }

            try
            {
                _unpacked = Unpack();
            }
            catch (Exception ex)
            {
                // Without its own file a channel falls back to the address in the settings, so this is worth
                // saying once but nowhere near fatal: never let artwork decide whether there are channels.
                _logger.LogWarning(ex, "ZDF Live TV: could not unpack the bundled channel logos into {Folder}; affected channels fall back to the configured logo address", Folder);

                _unpacked = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            return _unpacked;
        }
    }

    private Dictionary<string, string> Unpack()
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        Directory.CreateDirectory(Folder);

        foreach (var resourceName in typeof(ChannelLogoAssets).Assembly.GetManifestResourceNames())
        {
            // The build turns Assets/logo_one.png into <namespace>.Assets.logo_one.png, so everything after
            // that marker is the file name - which keeps working whatever prefix the SDK decides to give it.
            if (!resourceName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)
                || resourceName.IndexOf(AssetMarker, StringComparison.Ordinal) < 0)
            {
                continue;
            }

            var fileName = resourceName[(resourceName.LastIndexOf(AssetMarker, StringComparison.Ordinal) + AssetMarker.Length)..];
            var target = Path.Combine(Folder, fileName);

            using (var resource = typeof(ChannelLogoAssets).Assembly.GetManifestResourceStream(resourceName))
            {
                if (resource is null)
                {
                    continue;
                }

                // Same size means the same logo: an update that does not change the artwork should not have
                // to rewrite it, and a channel already pointing at this path keeps working either way.
                if (File.Exists(target) && new FileInfo(target).Length == resource.Length)
                {
                    paths[fileName] = target;
                    continue;
                }

                // Through a temporary name, so a crash mid-write cannot leave half a PNG where the server is
                // about to look for it.
                var temporary = target + ".tmp";
                using (var output = File.Create(temporary))
                {
                    resource.CopyTo(output);
                }

                File.Move(temporary, target, true);
                paths[fileName] = target;
            }
        }

        _logger.LogDebug(
            "ZDF Live TV: {Count} bundled channel logo(s) in {Folder}",
            paths.Count,
            Folder);

        return paths;
    }
}
