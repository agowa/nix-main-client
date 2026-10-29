{ config, lib, pkgs, inputs, ... }:

{
#  xdg.configFile."systemd/system/auditd.service.d/overrides.conf".text = ''
#  [Unit]
#  RefuseManualStop=no
#'';

  systemd.coredump.enable = false;
#  systemd.services.auditd.unitConfig = {
#    RefuseManualStop = "no";
#  };
#  systemd.automounts = [
#    {
#      enable = true;
#      name = "mnt-storage001.local.automount";
#      description = "Automount storage001.local";
#      where = "/mnt/storage001.local";
#      automountConfig = {
#        TimeoutIdleSec = 0;
#      };
#      wantedBy = [
#        "multi-user.target"
#      ];
#    }
#  ];
#  systemd.mounts = [
#    {
#      name = "mnt-storage001.local.mount";
#      wantedBy = [
#        "remote-fs.target"
#        "multi-user.target"
#      ];
#      before = [
#        "remote-fs.target"
#      ];
#      description = "Storage001 SSHFS";
#      what = "user@storage001.fritz.box:/mnt/data";
#      where = "/mnt/storage001.local";
#      type = "fuse." + pkgs.sshfs-fuse.meta.mainProgram;
#      options = "_netdev,rw,reconnect,port=2222,nosuid,allow_other,uid=" + (toString config.users.users.user.uid) +",gid=" + (toString config.users.groups.users.gid) + ",follow_symlinks,idmap=user,default_permissions,identityfile=/root/.ssh/user_storage001.local_automount_id_ed25519";
#    }
#  ];
  systemd.services."user@".serviceConfig = {
    Delegate = "yes";
  };

  systemd.services."systemd-user-sessions".after = lib.mkForce [];

  system.nssDatabases.hosts = lib.mkForce [
    "mymachines"
    #"resolve [!UNAVAIL=return]"
    "resolve"
    "files"
    "myhostname"
    "dns"
  ];

  xdg.portal.xdgOpenUsePortal = true; # Sets environment variable NIXOS_XDG_OPEN_USE_PORTAL to 1 This will make xdg-open use the portal to open programs, which resolves bugs involving programs opening inside FHS envs or with unexpected env vars set from wrappers. See https://github.com/NixOS/nixpkgs/issues/160923 for more info.
}
