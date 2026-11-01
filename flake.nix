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
    # The revision is pinned in the URL itself because flake.lock is gitignored,
    # so it cannot pin this input for us. Update by replacing the rev (then run
    # `nix flake update --no-write-lock-file` locally to see what changes).
    sops-nix.url = "github:Mic92/sops-nix/60182817ee048ac94b4c68ea76d5ef2e8aac730d";
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
