# SABnzbd - Usenet downloader.
#
# The web UI stays on localhost (no firewall port is opened); reach it through an SSH tunnel:
#   ssh -L 8085:127.0.0.1:8085 PC-001
#
# The provider credentials and the API key are *not* in this file. They live encrypted in
# ../secrets/secrets.yaml, are rendered into /run/secrets at activation (readable only by the
# sabnzbd user) and substituted into the generated ini while the service starts, so they never
# end up in the nix store, in a world-readable config file or in this public repository.
{ config, lib, pkgs, inputs, ... }:

let
  cfg = config.services.sabnzbd;

  secretNames = [
    "sabnzbd/api_key"
    "sabnzbd/newshosting/username"
    "sabnzbd/newshosting/password"
  ];
in {
  # Only while SABnzbd runs: a declared secret has to be decryptable at activation even when
  # nothing reads it, which is why the build-vm variant in modules/vm.nix switches this service off
  # instead of leaving the credentials required for a machine that cannot have PC-001's host key.
  sops.secrets = lib.mkIf cfg.enable (lib.genAttrs secretNames (name: {
    owner = cfg.user; # mode stays 0400, so only root and sabnzbd can read the value
    restartUnits = [ "sabnzbd.service" ]; # the ini is regenerated in preStart, a restart re-reads everything
  }));

  services.sabnzbd = {
    enable = true;

    # Must stay null to get the generated configuration: with this system's stateVersion of 25.05
    # the module otherwise defaults to /var/lib/sabnzbd/sabnzbd.ini, in which mode everything below
    # (settings, secretValues) is silently ignored and no preStart merge happens at all.
    configFile = null;

    # Keep the nix configuration as the single source of truth: the ini is installed read-only and
    # rebuilt on every start. SABnzbd notices this and logs a warning about the read-only config,
    # which is expected. Set to true to let the web UI persist its own changes (they are still
    # overridden by `settings` on the next restart).
    allowConfigWrite = false;

    settings = {
      misc = {
        host = "127.0.0.1"; # listen on loopback only
        port = 8085; # 8080 is already taken by BiglyBT, see modules/firewall.nix
        inet_exposure = "none"; # no API or web access from non-local addresses

        # Stable key for the JSON API (Radarr/Sonarr, sabnzbd-cli, ...). Without it SABnzbd would
        # generate a new one on every start and could not store it in this read-only config.
        api_key = "@sabnzbd-api-key@";

        permissions = "770";
      };

      servers.newshosting = {
        # name, host and displayname all have no default in the module and the ini generator reads
        # every declared option, so all three must be set.
        name = "newshosting.com";
        displayname = "Newshosting";
        host = "news.newshosting.com";
        port = 563; # NNTP over TLS, the only port SABnzbd dials out to
        ssl = true;
        ssl_verify = "strict";
        username = "@sabnzbd-newshosting-username@";
        # Values are substituted as plain text, so a password containing characters that ConfigObj
        # quotes ( ' " # ; , ) would corrupt the generated ini. Use secretFiles for such passwords.
        password = "@sabnzbd-newshosting-password@";
      };
    };

    secretValues = {
      "@sabnzbd-api-key@" = config.sops.secrets."sabnzbd/api_key".path;
      "@sabnzbd-newshosting-username@" = config.sops.secrets."sabnzbd/newshosting/username".path;
      "@sabnzbd-newshosting-password@" = config.sops.secrets."sabnzbd/newshosting/password".path;
    };
  };

  # SABnzbd creates its download directory with mode (0700), which leaves members of the sabnzbd
  # group unable to access it.
  # Gated on the service: any declaration under systemd.services.<name> makes NixOS generate that
  # unit, and it has to be gated as a whole attribute - gating only the ExecStartPost leaf still
  # creates systemd.services.sabnzbd, which is enough to leave an empty sabnzbd.service behind in
  # configurations without SABnzbd (the build-vm variant among them).
  systemd.services.sabnzbd = lib.mkIf cfg.enable {
    serviceConfig.ExecStartPost =
      "${pkgs.coreutils}/bin/chmod 0770 /var/lib/${cfg.stateDir}/Downloads /var/lib/${cfg.stateDir}/Downloads/complete /var/lib/${cfg.stateDir}/Downloads/incomplete";
  };
}
