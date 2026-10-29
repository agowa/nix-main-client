{ config, lib, pkgs, inputs, ... }:

{
  # TPM2
  security.tpm2.enable = true;
  security.tpm2.pkcs11.enable = true;  # expose /run/current-system/sw/lib/libtpm2_pkcs11.so
  security.tpm2.tctiEnvironment.enable = true;  # TPM2TOOLS_TCTI and TPM2_PKCS11_TCTI env variables

  # Allow unfree packages
  nixpkgs.config.allowUnfree = true;

  # As we're also "playing" with modifying/updateing nixpkgs ourselves apply the recommended default value
  # to avoid having nix reuse an older cached version when we tried to update a fetched file within a package
  # and want to provoke an error showing the new hash value.
  # See https://github.com/NixOS/nix/issues/969 and
  # https://nixos.org/manual/nixpkgs/unstable/#opt-fetchedSourceNameDefault for why this is required.
#  nixpkgs.config.fetchedSourceNameDefault = "full";
#  nixpkgs.config.fetchedSourceNameDefault = "versioned";

#  nixpkgs.config.enableParallelBuildingByDefault = true;
}
