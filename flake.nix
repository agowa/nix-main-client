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

  outputs = { self, ... }@inputs: {
    nixosConfigurations.PC-001 = inputs.nixpkgs.lib.nixosSystem {
      system = "x86_64-linux";

      specialArgs = {
        inherit inputs;
      };

      modules = [
        ./configuration.nix
      ];
    };
  };
}
