# Throwaway virtual machines of this configuration:
#
#   sudo nixos-rebuild build-vm --flake .#PC-001     # only builds ./result/bin/run-PC-001-vm
#   ./result/bin/run-PC-001-vm                       # boots it; console in the QEMU window
#
# Log in as root or user with the password below. The image is written to
# ./PC-001.qcow2 next to where the runner is started (NIX_DISK_IMAGE overrides that) and a
# throwaway image can simply be deleted: `rm -f PC-001.qcow2`.
#
# How this works: nixpkgs evaluates this configuration a second time with
# nixos/modules/virtualisation/qemu-vm.nix plus virtualisation.vmVariant (this file) on top and
# puts the resulting start script into config.system.build.vm, which is what build-vm builds.
# Two consequences worth knowing:
#
#   * vmVariant is an ordinary module inside that second evaluation, so it sees every option of
#     the real system - and overriding something a module here already sets needs lib.mkForce,
#     otherwise the two equal-priority definitions conflict at eval time.
#   * The VM is a copy of the configuration, never of the data. qemu-vm.nix replaces
#     fileSystems, swapDevices and boot.initrd.luks.devices with mkVMOverride values, so none of
#     PC-001's disks (RAID5 volume, its LUKS containers, /boot) exist in the guest; it boots a
#     plain ext4 image directly through -kernel/-initrd. Everything that would have lived on
#     those mounts is simply empty here, which is why the services below are switched off rather
#     than left to start against missing directories.
#
# What is deliberately *not* undone: the secrets. Every sops secret in this repository is
# declared behind the enable flag of the service that reads it (modules/tor.nix, modules/i2pd.nix,
# modules/p2pool.nix, modules/sabnzbd.nix, modules/services.nix), so switching those services off
# here also drops every secret declaration. With `sops.secrets == { }` sops-nix skips its whole
# config block (its module wraps it in mkIf (cfg.secrets != { })), no sops-install-secrets unit is
# built and the guest therefore never asks for PC-001's SSH host key - which it cannot have, since
# decrypting with it would mean carrying a production secret into the VM.
{ config, lib, pkgs, inputs, ... }:

let
  # Wire the guest's sound card to the sound server of the machine that *launches* QEMU (the guest
  # runs its own PipeWire either way - modules/desktop.nix enables services.pipewire with
  # pulse.enable, and that part works in a VM regardless of this flag).
  #
  # "-audiodev pa" is resolved by QEMU when the VM starts, so it needs a PulseAudio/PipeWire
  # session in the environment of whoever runs run-PC-001-vm. That holds on PC-001's desktop and
  # not on a headless builder such as shellbox, where it would abort QEMU with
  # "audio: Could not init 'pa'"; there the same sound card is kept but left unconnected ("none").
  hostAudio = false;
in
{
  virtualisation.vmVariant = {

    # ---------------------------------------------------------------------------
    # Login. The passwords are in a public repository on purpose: these VMs hold no
    # data (see above), they exist to boot the configuration, and a locked machine
    # you cannot log into does not do that. Do not copy this block into a host config.
    # ---------------------------------------------------------------------------

    # Reset users from the configuration on every activation instead of keeping whatever
    # usermod did, which is what makes the password below stick on first boot.
    users.mutableUsers = false;
    users.users.root.password = "test123";
    users.users.user.password = "test123";

    # modules/services.nix turns password authentication off for PC-001, where the SSH keys in
    # modules/users.nix are enough. In a VM that would leave the passwords above usable only on
    # the console, so they are enabled here (the guest is on QEMU's private network and no host
    # port is forwarded to it).
    services.openssh.settings.PasswordAuthentication = lib.mkForce true;

    # ---------------------------------------------------------------------------
    # Resources. Sized for shellbox, where these VMs are built and tested: 4 vCPUs and
    # 15.6 GiB of RAM total, so the guest gets all four cores but stays well under what
    # the host has - it also has to survive a full system build next to the running VM.
    # Raise memorySize/cores for a run on PC-001 itself; both are baked into the runner
    # script (-m/-smp), so a rebuild is needed after changing them, and QEMU_OPTS cannot
    # override them (a second -m makes QEMU bail out).
    # ---------------------------------------------------------------------------

    virtualisation.memorySize = 8192; # MiB
    virtualisation.cores = 4;
    virtualisation.diskSize = 20 * 1024; # MiB, sparse: the qcow2 only grows as the guest writes

    # Only relevant with services.unifi.enable switched back on above: PC-001 gives the controller a
    # 4 GiB initial heap (modules/services.nix), which alone would be half of this VM.
    services.unifi.initialJavaHeapSize = lib.mkForce 1024;

    # A login prompt on the serial console. QEMU's default serial backend is a QEMU window, so on
    # a headless builder the VM is started with QEMU_OPTS="-display none -serial mon:stdio" (the
    # kernel already gets console=ttyS0 from qemu-vm.nix) - but nothing would answer there: this
    # configuration hands its graphical console to SDDM (modules/desktop.nix), which also drops the
    # autovt@tty1 getty, and nixpkgs only enables console-getty inside containers. With this unit the
    # guest is reachable over serial as root/user with the password above.
    systemd.services."serial-getty@ttyS0" = {
      enable = true;
      wantedBy = [ "getty.target" ];
    };

    virtualisation.qemu.options = [
      "-audiodev ${if hostAudio then "pa" else "none"},id=snd0"
      "-device ich9-intel-hda"
      "-device hda-duplex,audiodev=snd0"
    ];

    # ---------------------------------------------------------------------------
    # Services that need a sops secret (see the header) - switching them off is what
    # makes the guest decrypt nothing. They are also the services that make no sense in
    # a throwaway VM: relaying Tor traffic, mining or syncing a blockchain from scratch.
    # ---------------------------------------------------------------------------

    # Onion service for monerod/p2pool + bridge; secret: tor/monerod_hs_ed25519_secret_key.
    services.tor.enable = lib.mkForce false;
    # I2P tunnels and their destination key set; secret: i2p/monero_mainnet_keys.
    services.i2pd.enable = lib.mkForce false;
    # monerod's anonymous-inbound addresses; secrets: monero/tor_hostname, monero/i2p_hostname.
    services.monero.enable = lib.mkForce false;
    # Payout address + the two addresses above in its EnvironmentFile; secret: p2pool/wallet_address.
    services.p2pool.enable = lib.mkForce false;
    # API key and Newshosting credentials; secrets: sabnzbd/*.
    services.sabnzbd.enable = lib.mkForce false;

    # The UniFi network controller. It is not a service a throwaway VM can exercise (it adopts real
    # access points, listens on a dozen ports and wants gigabytes of RAM), and it drags MongoDB
    # along with it: services.unifi.mongodbPackage defaults to mongodb-7_0, which has no substitute
    # on cache.nixos.org at this revision, so every VM build would spend hours compiling MongoDB from
    # source first. Its 4 GiB Java heap for PC-001 is shrunk below rather than deleted, so that
    # re-enabling this line stays the only thing needed to test the controller in a VM again.
    services.unifi.enable = lib.mkForce false;

    # Tor's pluggable-transport bridge. No secret, but it is the same anonymity-network
    # participation as services.tor and would only log errors here.
    services.snowflake-proxy.enable = lib.mkForce false;

    # GPU driver stack. modules/hardware.nix fills hardware.graphics.extraPackages with Intel
    # drivers for PC-001's iGPU (QSV/VAAPI/OpenCL); the guest gets virtio-gpu instead and renders
    # through Mesa/llvmpipe, so none of them apply here - and dropping them keeps a big chunk of
    # the host closure out of the VM build. One of them is also currently unbuildable at all:
    # intel-ocl's SRB5.0_linux64.zip answers 403 on registrationcenter-download.intel.com (see
    # nixpkgs#166886), so a VM that carried it could not be built anywhere, shellbox included.
    # Mesa itself stays: it is hardware.graphics.package, not an extra package.
    hardware.graphics.extraPackages = lib.mkForce [ ];
    hardware.graphics.extraPackages32 = lib.mkForce [ ];

    # Drive health monitoring. There is nothing to interrogate here: the guest sees a virtio disk
    # with no SMART data, so smartd only ever ends up as a failed unit and leaves the machine in
    # state "degraded" - which would hide the failure of anything that actually matters.
    services.smartd.enable = lib.mkForce false;

    # Scrubbing the RAID5 volume: services.btrfs.autoScrub installs one timer per mounted btrfs
    # filesystem and nixpkgs asserts that there is at least one, which cannot be true here because
    # qemu-vm.nix replaces fileSystems (see the header). The host keeps scrubbing its real volume.
    services.btrfs.autoScrub.enable = lib.mkForce false;

    # ---------------------------------------------------------------------------
    # Networking. modules/networking.nix is written for PC-001's two NICs: DPDK
    # openvswitch on eno1/eno2 plus the ovsbr0 bridge and its VLANs, matched by permanent
    # MAC address and interface name. None of that exists in a VM - the units would only
    # fail against interfaces that never appear - so the guest gets one DHCP unit for the
    # virtio NIC QEMU gives it (QEMU's user mode network hands out 10.0.2.x with DNS on
    # 10.0.2.3), which is what makes the VM able to reach the outside world.
    # ---------------------------------------------------------------------------

    virtualisation.vswitch.enable = lib.mkForce false;

    systemd.network.networks."99-vm-dhcp" = {
      matchConfig.Name = "ens* enp* eth*";
      networkConfig = {
        DHCP = "yes";
        IPv6AcceptRA = "yes";
      };
    };

  };
}
