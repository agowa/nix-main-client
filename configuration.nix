# Edit this configuration file to define what should be installed on
# your system.  Help is available in the configuration.nix(5) man page
# and in the NixOS manual (accessible by running ‘nixos-help’).

{ config, lib, pkgs, inputs, ... }:

{
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
      ./modules/programs.nix
      ./modules/virtualisation.nix
      ./modules/services.nix
      ./modules/i2pd.nix
      ./modules/tor.nix
      ./modules/security.nix
      ./modules/system.nix
      ./modules/systemd.nix
    ];

}

