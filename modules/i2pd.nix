{ config, lib, pkgs, inputs, ... }:

{
  services.i2pd = {
    enable = true;
    yggdrasil.enable = false;
#  websocket.enable = true;
    upnp.enable = true;
    reseed.verify = false;
    reseed.file = "${../i2pseeds.su3}"; # wget --user-agent="Wget/1.11.4" https://reseed.stormycloud.org/i2pseeds.su3
    proto.socksProxy.enable = true;
    proto.i2cp.enable = true;
    proto.i2pControl.enable = true;
    proto.http.enable = true; # WebUI
    port = 19410;
    enableIPv6 = true;
    enableIPv4 = true;
    ntcp2 = {
      enable = true;
      published = true;
    };
#    ssu2.enable = true;
#    ssu2.published = true;
    proto.httpProxy.enable = true;
  };
}
