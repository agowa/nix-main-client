{ config, lib, pkgs, inputs, ... }:

{
  virtualisation.lxc.enable = true;
  virtualisation.lxc.unprivilegedContainers = true;
  virtualisation.podman.enable = true;
  virtualisation.podman.dockerSocket.enable = true;
  virtualisation.podman.autoPrune.enable = true; # If enabled, a systemd timer will run podman system prune -f as specified by the dates option.
  virtualisation.podman.dockerCompat = true;
  virtualisation.containers.enable = true;
  virtualisation.containers.ociSeccompBpfHook.enable = true;
  virtualisation.containerd.enable = true;
  virtualisation.waydroid.enable = true;
  virtualisation.libvirtd.enable = true;
  virtualisation.libvirtd.qemu.package = pkgs.qemu_full;
  virtualisation.libvirtd.qemu.swtpm.enable = true;
  virtualisation.libvirtd.qemu.vhostUserPackages = [
    pkgs.virtiofsd
  ];
#  virtualisation.libvirtd.qemu.ovmf.packages = [
#    pkgs.OVMFFull.fd
#    pkgs.pkgsCross.aarch64-multiplatform.OVMF.fd
#  ];
}
