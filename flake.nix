{
  description = "NixOS configuration";

  inputs = {
    nixpkgs.url = "path:./nixpkgs/nixos-26.05";
    nixos-25-05.url = "path:./nixpkgs/nixos-25.05";
    nixos-25-11.url = "path:./nixpkgs/nixos-25.11";
    nixos-26-05.url = "path:./nixpkgs/nixos-26.05";
    self.submodules = true;
    self.lfs = true;
  };

  outputs = { self, nixpkgs, nixos-25-05, nixos-25-11, nixos-26-05, ... }: {
    nixosConfigurations.PC-001 = nixpkgs.lib.nixosSystem {
      system = "x86_64-linux";
      modules = [
        ./configuration.nix
      ];
    };
  };
}
