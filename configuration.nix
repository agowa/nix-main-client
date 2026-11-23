# Edit this configuration file to define what should be installed on
# your system.  Help is available in the configuration.nix(5) man page
# and in the NixOS manual (accessible by running ‘nixos-help’).

{ config, lib, pkgs, inputs, ... }:

{
  options.virtualisation.isVmVariant = lib.mkOption {
    type = lib.types.bool;
    default = false;
    description = "Whether the VM variant is being built";
  };

  imports =
    [ # Include the results of the hardware scan.
      ./hardware-configuration.nix
      ./modules/boot.nix
      ./modules/filesystems.nix
      ./modules/hardware.nix
      ./modules/i18n.nix
      ./modules/desktop.nix
      ./modules/users.nix
      ./modules/environment.nix
      ./modules/networking.nix
      ./modules/firewall.nix
      ./modules/sops.nix
      ./modules/programs.nix
      ./modules/virtualisation.nix
      ./modules/vm.nix
      ./modules/services.nix
      ./modules/i2pd.nix
      ./modules/tor.nix
      ./modules/sabnzbd.nix
      ./modules/p2pool.nix
      ./modules/security.nix
      ./modules/system.nix
      ./modules/systemd.nix
    ];

}

