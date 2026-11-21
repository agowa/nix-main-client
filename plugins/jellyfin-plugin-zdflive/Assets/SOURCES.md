# Bundled channel logos

PNG renderings of broadcaster logos, embedded in the plugin assembly (`csproj` -> `Assets\*.png`) and
unpacked at runtime by `ChannelLogoAssets`. They exist because artwork that has to be downloaded is
artwork that can fail to arrive: Jellyfin fetches a channel's remote logo lazily, when something first
asks for the tile, and one failed download costs both an error response *and* the stored image reference
(`LibraryManager.ConvertImageToLocal(..., removeOnFailure: true)`), which leaves a broken tile until the
artwork is written again. A file that is already on disk cannot fail like that.

Only channels the configured logo host (`LogoTemplate`, `https://static.epg.best/de/{id}.png` by default)
does not carry are bundled here; everything else keeps using the template so the setting stays meaningful.

| File | Rendered from | Source |
| --- | --- | --- |
| `logo_3sat.png` | `thumb.php?f=3sat_2019.svg&w=256` | [Commons:File:3sat 2019.svg](https://commons.wikimedia.org/wiki/File:3sat_2019.svg) |
| `logo_ndr.png` | `thumb.php?f=NDR_Fernsehen_Logo.svg&w=256` | [Commons:File:NDR Fernsehen Logo.svg](https://commons.wikimedia.org/wiki/File:NDR_Fernsehen_Logo.svg) |
| `logo_one.png` | `thumb.php?f=One_2022.svg&w=256` | [Commons:File:One 2022.svg](https://commons.wikimedia.org/wiki/File:One_2022.svg) |
| `logo_radio_bremen.png` | `thumb.php?f=Radio_Bremen_TV_Logo_2015.svg&w=256` | [Commons:File:Radio Bremen TV Logo 2015.svg](https://commons.wikimedia.org/wiki/File:Radio_Bremen_TV_Logo_2015.svg) |
| `logo_sr.png` | `thumb.php?f=Saarl%C3%A4ndischer_Rundfunk_Logo_2023.svg&w=256` | [Commons:File:Saarländischer Rundfunk Logo 2023.svg](https://commons.wikimedia.org/wiki/File:Saarl%C3%A4ndischer_Rundfunk_Logo_2023.svg) |
| `logo_bundestag.png` | `thumb.php?f=Deutscher_Bundestag_logo.svg&w=256` | [Commons:File:Deutscher Bundestag logo.svg](https://commons.wikimedia.org/wiki/File:Deutscher_Bundestag_logo.svg) |

All six source files are tagged **Public domain** on Wikimedia Commons (word marks and, for the Bundestag,
its own emblem: the eagle Prof. Ludwig Gies designed in 1953, redrawn flat by büro uebele in 2008), which is
why they may be redistributed here. `thumb.php` renders the SVG at the asked width - 256 px was picked because
a channel tile is never larger than that in the web client and the whole set stays under 50 kB inside the
assembly.

Commons holds a second Bundestag file, `File:Logo of the Deutscher Bundestag.svg`, whose name sounds like the
better fit for this purpose and which is the wrong picture twice over: it is an Inkscape outline that
rasterises to a few thin strokes on white with no eagle in it at all, and where the file used here is used by
thirty or more Wikimedia pages - among them the English and German `Bundestag` articles - that one is used by
two. It is also the eagle bundestag.de serves today: its own inline logo measures 63.23 x 55 and this file
400.006 x 347.939, the same 1.1496 to one, so the two are the same drawing redrawn flat.

To re-render one (the width has to be repeated, `Special:FilePath` ignores it):

```sh
curl -sSL -A 'nix-main-client-plugin-logos/1.0' \
     -o logo_one.png 'https://commons.wikimedia.org/w/thumb.php?f=One_2022.svg&w=256'
```
