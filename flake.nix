{
  description = "NixOS configuration";

  inputs = {
    nixpkgs.url = "path:./nixpkgs/nixos-26.05";
    # nixos-25-05.url = "path:./nixpkgs/nixos-25.05"; # unused
    # nixos-25-11.url = "path:./nixpkgs/nixos-25.11"; # unused
    # nixos-26-05.url = "path:./nixpkgs/nixos-26.05"; # unused
    nixos-unstable.url = "path:./nixpkgs/nixos-unstable";

    # Secrets management for this public repository: files under ./secrets are
    # encrypted with sops and decrypted on the target machine at activation.
    # Deliberately not pinned here - the local flake.lock does the pinning, and that file is
    # gitignored on purpose because it is local state only. Move the input with
    # `nix flake update sops-nix`, which records the new rev in your own flake.lock.
    sops-nix.url = "path:./nixpkgs/sops-nix";
    sops-nix.inputs.nixpkgs.follows = "nixpkgs";

    self.submodules = true;
    self.lfs = true;
  };

  outputs = { self, ... }@inputs:
    let
      system = "x86_64-linux";
      pkgs = import inputs.nixpkgs { inherit system; };
    in
    {
      nixosConfigurations.PC-001 = inputs.nixpkgs.lib.nixosSystem {
        system = "x86_64-linux";

        specialArgs = {
          inherit inputs;
        };

        modules = [
          ./configuration.nix
        ];
      };

      # The ZDF Live TV Jellyfin plugin (plugins/jellyfin-plugin-zdflive). Exposed as a package so that
      # `nix build .#jellyfin-plugin-zdf-live` works; modules/jellyfin-zdf.nix installs this same
      # derivation and defaults to callPackage on the very same directory.
      packages.${system}.jellyfin-plugin-zdf-live =
        pkgs.callPackage ./plugins/jellyfin-plugin-zdflive/package.nix { };

      # Dotnet SDK matching jellyfin 10.11.x, for iterating on the plugin without a Nix build per try:
      #   nix develop .#plugin-dev
      #   cd plugins/jellyfin-plugin-zdflive && dotnet build Jellyfin.Plugin.ZdfLive.csproj -c Release
      devShells.${system}.plugin-dev = pkgs.mkShell {
        packages = [ pkgs.dotnetCorePackages.sdk_9_0 pkgs.cacert ];

        DOTNET_CLI_TELEMETRY_OPTOUT = "1";
        DOTNET_NOLOGO = "1";

        # NuGet may not write into the read-only store, so the package cache gets a home outside it.
        shellHook = ''
          export NUGET_PACKAGES="''${XDG_CACHE_HOME:-$HOME/.cache}/nuget-jellyfin-plugins"
          mkdir -p "$NUGET_PACKAGES"
        '';
      };
    };
}
