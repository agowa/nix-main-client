{
  description = "NixOS configuration";

  inputs = {
    nixpkgs.url = "path:./nixpkgs/nixos-26.05";
    # nixos-25-05.url = "path:./nixpkgs/nixos-25.05"; # unused
    # nixos-25-11.url = "path:./nixpkgs/nixos-25.11"; # unused
    # nixos-26-05.url = "path:./nixpkgs/nixos-26.05"; # unused
    nixos-unstable.url = "path:./nixpkgs/nixos-unstable";

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
