{
  lib,
  buildDotnetModule,
  dotnetCorePackages,
}:

buildDotnetModule (finalAttrs: {
  pname = "jellyfin-plugin-zdf-live";
  version = "1.0.0";

  src = ./.;
  projectFile = "Jellyfin.Plugin.ZdfLive.csproj";
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
  installPhase = ''
    runHook preInstall

    pluginDir="$out/lib/jellyfin/plugins/ZdfLive"
    install -d "$pluginDir"
    install -m 0644 ./publish/Jellyfin.Plugin.ZdfLive.dll "$pluginDir/"
    install -m 0644 ./meta.json "$pluginDir/"

    runHook postInstall
  '';

  dontFixup = true;

  meta = {
    description = "Jellyfin Live TV tuner for the ZDF live channels (ZDF, ZDFneo, ZDFinfo, 3sat, ARTE, KiKA, phoenix)";
    homepage = "https://www.zdf.de/live-tv";
    license = lib.licenses.gpl3Only;
    maintainers = [ ];
    platforms = lib.platforms.all;
  };
})
