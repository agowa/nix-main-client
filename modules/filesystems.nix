{ config, lib, pkgs, inputs, ... }:

{
  # Cryptsetup and non-root filesystems
  # Improve performance on SSDs
  boot.initrd.luks.devices.luks-308704a3-5f1f-4cac-99ba-45d040ec57b9 = {
    # System partition
    allowDiscards = true;
    bypassWorkqueues = true;
    device = "/dev/disk/by-uuid/308704a3-5f1f-4cac-99ba-45d040ec57b9";
  };
  boot.initrd.luks.devices.luks-57e4edfa-54c0-4b4a-985a-adbe5fd6d497 = {
    # swap
    allowDiscards = false;
    bypassWorkqueues = true;
    device = "/dev/disk/by-uuid/57e4edfa-54c0-4b4a-985a-adbe5fd6d497";
  };
  # See https://github.com/NixOS/nixpkgs/issues/459869
  environment.etc.crypttab = let disk = {
    # raid 5 data partition
    # /dev/vg-019cf9c3-7ed8-70e1-bb36-35e054c42b12/lv-019cf9c3-7ed8-70e1-bb36-35e054c42b12-raid5
    name = "luks-019cf9c3-7ed8-70e1-bb36-35e054c42b12";
    uuid = "4f1280e8-83f9-4dad-911f-07bf124d5ef0";
  }; in {
    text = ''
      ${disk.name} UUID=${disk.uuid} none luks,timeout=10s
    '';
  };

  fileSystems."/".options = [
    "lazytime"
    "strictatime"
  ];
  fileSystems."/mnt/FA380FB6380F7145" = {
    device = "/dev/disk/by-uuid/FA380FB6380F7145";
    fsType = "ntfs-3g";
    options = [
      "nofail"
      "lazytime"
      "strictatime"
      "nodev"
      "nosuid"
      ("uid=" + (toString config.users.users.user.uid))
      ("gid=" + (toString config.users.groups.users.gid))
    ];
  };
  fileSystems."/mnt/vg-019cf9c3-7ed8-70e1-bb36-35e054c42b12/lv-019cf9c3-7ed8-70e1-bb36-35e054c42b12-raid5" = {
    device = "/dev/disk/by-uuid/2df491cf-b27c-41cd-ad88-2aa2b25278ca";
    encrypted = {
      blkDev = "/dev/disk/by-uuid/4f1280e8-83f9-4dad-911f-07bf124d5ef0";
      enable = true;
      label = "luks-019cf9c3-7ed8-70e1-bb36-35e054c42b12";
    };
    fsType = "btrfs";
    options = [
      "nofail"
      "lazytime"
      "strictatime"
      "nodev"
      "nosuid"
    ];
  };
}
