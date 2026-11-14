{
  lib,
  buildDotnetModule,
  dotnetCorePackages,
}:

buildDotnetModule (finalAttrs: {
  pname = "jellyfin-plugin-twitch-live";
  version = "1.0.0";

  src = ./.;
  projectFile = "Jellyfin.Plugin.TwitchLive.csproj";
  nugetDeps = ./nuget-deps.json;

  dotnet-sdk = dotnetCorePackages.sdk_9_0;

  # `dotnet build` would also copy the compile-only Jellyfin assemblies when their packages are
  # referenced without ExcludeAssets; publishing keeps the output to what the plugin really needs.
  buildPhase = ''
    runHook preBuild
    dotnet publish ${finalAttrs.projectFile} -c Release --no-restore --output ./publish
    runHook postBuild
  '';

  # The result is laid out as one Jellyfin plugin folder: drop it into <dataDir>/plugins/<name>/.
  # The configuration page ships inside the assembly as an embedded resource, so only two files go in.
  installPhase = ''
    runHook preInstall

    pluginDir="$out/lib/jellyfin/plugins/TwitchLive"
    install -d "$pluginDir"
    install -m 0644 ./publish/Jellyfin.Plugin.TwitchLive.dll "$pluginDir/"
    install -m 0644 ./meta.json "$pluginDir/"

    runHook postInstall
  '';

  dontFixup = true;

  meta = {
    description = "Jellyfin Live TV tuner for Twitch channels, with the channel list editable at runtime";
    homepage = "https://www.twitch.tv/directory";
    license = lib.licenses.gpl3Only;
    maintainers = [ ];
    platforms = lib.platforms.all;
  };
})
