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

    /// <summary>File names this assembly actually contains, so a typo can be told from a failed write.</summary>
    private readonly HashSet<string> _embedded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Names already complained about, so one mistake is said once rather than per channel list.</summary>
    private readonly HashSet<string> _reported = new(StringComparer.OrdinalIgnoreCase);

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

        if (unpacked.TryGetValue(fileName, out var path))
        {
            if (File.Exists(path))
            {
                return path;
            }

            // The folder is inside the server's data directory, so anything that cleans that up takes these
            // files with it while the process is running. Forgetting what was unpacked makes the next call
            // write them again rather than hand out a path that is not there - which the server would then
            // store on a channel and show as a broken tile.
            lock (_gate)
            {
                _unpacked = null;
            }

            return EnsureUnpacked().TryGetValue(fileName, out var written) && File.Exists(written) ? written : null;
        }

        // Nothing usable for that name. Two different mistakes lead here and they need different answers, so
        // only the one Unpack() cannot report itself is said here: a name this assembly does not contain is a
        // typo in ZdfChannelCatalog or an Assets/ file that did not make it into the build, and the channel
        // would sit without a picture forever with nothing to point at. A name that IS embedded means the
        // write failed, which Unpack() has already said along with why.
        lock (_gate)
        {
            if (!_embedded.Contains(fileName) && _reported.Add(fileName))
            {
                _logger.LogWarning("ZDF Live TV: the catalogue asks for the logo '{File}', which this build does not contain", fileName);
            }
        }

        return null;
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
        var assembly = typeof(ChannelLogoAssets).Assembly;
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // The build turns Assets/logo_one.png into <namespace>.Assets.logo_one.png, so everything after that
        // marker is the file name - which keeps working whatever prefix the SDK decides to give it. Collected
        // up front and remembered either way, because PathFor needs to know what this assembly holds before it
        // can tell a typo in the catalogue apart from a directory it could not write.
        var resources = new List<KeyValuePair<string, string>>();

        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var marker = resourceName.LastIndexOf(AssetMarker, StringComparison.Ordinal);
            if (marker < 0)
            {
                continue;
            }

            var fileName = resourceName[(marker + AssetMarker.Length)..];
            resources.Add(new KeyValuePair<string, string>(fileName, resourceName));
            _embedded.Add(fileName);
        }

        Directory.CreateDirectory(Folder);

        foreach (var pair in resources)
        {
            var target = Path.Combine(Folder, pair.Key);

            try
            {
                using var resource = assembly.GetManifestResourceStream(pair.Value);
                if (resource is null)
                {
                    continue;
                }

                // Same size means the same logo: an update that does not change the artwork should not have to
                // rewrite it, and a channel already pointing at this path keeps working either way.
                if (!File.Exists(target) || new FileInfo(target).Length != resource.Length)
                {
                    // Through a temporary name, so a crash mid-write cannot leave half a PNG where the server
                    // is about to look for it. Unique per write rather than one shared ".tmp": unpacking runs on
                    // demand from whichever code asks for a logo first, and two of those at once - the channel
                    // list and the artwork repair that follows it - would otherwise be writing into the same
                    // file and racing over who gets to rename it.
                    var temporary = target + "." + Path.GetRandomFileName() + ".tmp";

                    try
                    {
                        using (var output = File.Create(temporary))
                        {
                            resource.CopyTo(output);
                        }

                        File.Move(temporary, target, true);
                    }
                    catch (Exception)
                    {
                        // A half-written temporary file is nobody's business but clutter, so take it out of the
                        // way before the outer handler logs the failure.
                        if (File.Exists(temporary))
                        {
                            File.Delete(temporary);
                        }

                        throw;
                    }
                }

                paths[pair.Key] = target;
            }
            catch (Exception ex)
            {
                // One logo that will not write must not cost the others - and no channel is lost over it, it
                // just has no picture until this works out.
                _logger.LogWarning(ex, "ZDF Live TV: could not unpack the bundled logo {File} into {Folder}", pair.Key, Folder);
            }
        }

        _logger.LogDebug(
            "ZDF Live TV: {Count} of {Total} bundled channel logo(s) available in {Folder}",
            paths.Count,
            resources.Count,
            Folder);

        return paths;
    }
}
