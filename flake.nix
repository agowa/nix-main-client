{
  description = "NixOS configuration";

  inputs = {
    nixpkgs.url = "path:./nixpkgs/nixos-26.05";
    nixos-25-05.url = "path:./nixpkgs/nixos-25.05";
    nixos-25-11.url = "path:./nixpkgs/nixos-25.11";
    nixos-26-05.url = "path:./nixpkgs/nixos-26.05";
  };

  outputs = { self, nixpkgs, nixos-25-05, nixos-25-11, nixos-26-05, ... }: {
    nixosConfigurations.pc-001 = nixpkgs.lib.nixosSystem {
      system = "x86_64-linux";
      modules = [
        ./configuration.nix
      ];
    };
  };
}
