{ config, lib, pkgs, inputs, ... }:

{
  # This value determines the NixOS release from which the default
  # settings for stateful data, like file locations and database versions
  # on your system were taken. It‘s perfectly fine and recommended to leave
  # this value at the release version of the first install of this system.
  # Before changing this value read the documentation for this option
  # (e.g. man configuration.nix or on https://nixos.org/nixos/options.html).
  system.stateVersion = "25.05"; # Did you read the comment?

  # system.autoUpgrade.channel = "https://nixos.org/channels/nixos-unstable";
  system.autoUpgrade = {
    enable = false; # periodically runs "nixos-rebuild switch --upgrade"
    channel = "https://nixos.org/channels/nixos-${config.system.nixos.release}";
  };
  nix.gc.automatic = true;
  nix.gc.options = "--delete-older-than 30d";
  nix.gc.dates = "weekly";
  nix.optimise.automatic = true;
  nix.settings.experimental-features = ["nix-command flakes"];
  programs.nix-index = {
    enable = true;
    enableBashIntegration = false;
    enableZshIntegration = false;
    enableFishIntegration = false;
  };
}
