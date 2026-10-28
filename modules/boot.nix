{ config, lib, pkgs, inputs, ... }:

{
  # Bootloader.
  boot.loader.systemd-boot.enable = true;
  boot.loader.efi.canTouchEfiVariables = true;
  boot.initrd.systemd.enable = true;

  boot.kernel.sysctl."vm.max_map_count" = 2147483647;
  boot.kernel.sysctl."vm.swappiness" = 100;
  # Hugepages
  boot.kernel.sysctl."vm.nr_hugepages" = 3072; # 6 GB
  #boot.kernel.sysctl."vm.nr_hugepages" = 51200; # 100 GB
  #boot.kernel.sysctl."vm.nr_hugepages" = 102400; # 200 GB

  boot.kernelParams = [
#    "mem_encrypt=on" # AMD SME
    "amd_iommu=pgtbl_v1"
#    "amd_iommu=pgtbl_v2"
    "iommu=pt"
    "iommu=nobypass"
    "iommu.passthrough=0" # 0 - Use IOMMU translation for DMA.
    "iommu.strict=1" # 1 - Strict mode.
    "kvm.ignore_msrs=1"
    "kvm.enable_virt_at_load=1" # Avoid delay at start of 1st VM.
    "pci=realloc"
    "amd_pstate=guided"
    "rd.luks.options=discard"
    "bgrt_disable" # Don't display OEM logo after loading ACPI tables
  ];

  # load in 1st stage before root file system has been mounted.
  boot.initrd.kernelModules = [
    "nvme"
    "mpt3sas"
    "dm_log"
    "dm_cache"
    "dm_cache_smq"
    "dm_thin_pool"
    "dm_raid"
    "dm_integrity"
    "dm_snapshot"

    # These are reported as "builtin", however btrfs documentation states:
    # > Many kernels are configured with SHA256 as built-in and not as a module. The accelerated versions are however provided by the modules and must be loaded explicitly (modprobe sha256) before mounting the filesystem to make use of them. You can check in /sys/fs/btrfs/FSID/checksum which one is used. If you see sha256-generic, then you may want to unmount and mount the filesystem again.
    # Therefore listing these here explicitly just in case.
    "sha256"
    "sha512"
    "sha3"
    "blake2b"
    "crc32"
    "crc32c"

    "btrfs"
    "loop"
    "v4l2loopback"
#    "xe"
    "mtd_intel_dg"
#    "snd_hda_intel"
#    "drm"
#    "i915"
    "efi_pstore"
    "configfs"
    "amd64_edac"
    "sr_mod"
    "igb"
    "btusb"
  ];

  # load in 2nd stage after root file system has been mounted.
  boot.kernelModules = [
    "nvme"
    "mpt3sas"
    "dm_log"
    "dm_cache"
    "dm_cache_smq"
    "dm_thin_pool"
    "dm_raid"
    "dm_integrity"
    "dm_snapshot"

    # These are reported as "builtin", however btrfs documentation states:
    # > Many kernels are configured with SHA256 as built-in and not as a module. The accelerated versions are however provided by the modules and must be loaded explicitly (modprobe sha256) before mounting the filesystem to make use of them. You can check in /sys/fs/btrfs/FSID/checksum which one is used. If you see sha256-generic, then you may want to unmount and mount the filesystem again.
    # Therefore listing these here explicitly just in case.
    "sha256"
    "sha512"
    "sha3"
    "blake2b"
    "crc32"
    "crc32c"

    "btrfs"
    "loop"
    "v4l2loopback"
#    "xe"
    "mtd_intel_dg"
#    "snd_hda_intel"
#    "drm"
#    "i915"
    "efi_pstore"
    "configfs"
    "amd64_edac"
    "sr_mod"
    "igb"
    "btusb"
  ];

  boot.blacklistedKernelModules = [
    "ast"
  ];

  boot.initrd.services.lvm.enable = true; # required for cache_check binary to be placed at correct location for mounting of LVM volumes with an attached cache volume.
  services.lvm.boot.thin.enable = true; # required for chache_check binary too.
  services.lvm.dmeventd.enable = true;
#  services.multipath.enable = true;

  # Use latest kernel.
  # Use "nix repl" > ":l <nixpkgs>" > "pkgs.linuxPackages" [press tab twice to show all linux Packages]
  # To show the kernel version of one of them add ".kernel" to the of their full package name.
  boot.kernelPackages = pkgs.linuxPackages_latest;
#  boot.kernelPackages = pkgs.linuxPackages_testing;
}
