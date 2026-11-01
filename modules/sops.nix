# Secret management for a public repository.
#
# Secret values live in ../secrets/secrets.yaml, which is encrypted with sops and can be
# committed as-is: only the recipients' *public* keys are stored in .sops.yaml at the repo root.
# sops-nix renders each declared secret into a ramfs-backed file under /run/secrets during the
# activation script, which stage-2 runs before any service unit starts, so services can simply
# read their secrets from there.
#
# Editing a secret (requires the private half of one of the recipients):
#   nix shell nixpkgs#sops -c sops secrets/secrets.yaml
{ config, lib, pkgs, inputs, ... }:

{
  imports = [ inputs.sops-nix.nixosModules.sops ];

  sops = {
    defaultSopsFile = ../secrets/secrets.yaml;

    age.sshKeyPaths = [ "/etc/ssh/ssh_host_ed25519_key" ];
    # The host key above is converted into an age identity at activation time, which means this
    # machine decrypts with a key it already has and no long-lived secret key material needs to be
    # deployed by hand. Consequently the *public* half of that host key must be listed as a
    # recipient in .sops.yaml - if PC-001 is reinstalled and regenerates its host keys, the new
    # public key has to be added there (with any remaining recipient) and the file re-encrypted.
  };
}
