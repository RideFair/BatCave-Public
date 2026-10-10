# Mac app icon

`BatCave.png` is the transparent 1024-pixel source for `BatCave.icns`.
The corrected outer edge has no opaque white outline or corner artifacts.
Keep the intentional gray highlights inside the artwork.

To rebuild, resize the PNG with `sips -z SIZE SIZE` into a `.iconset` directory
using the standard `icon_16x16`, `icon_32x32`, `icon_128x128`, `icon_256x256`
and `icon_512x512` names, with both `.png` and `@2x.png` versions. The Retina
versions use twice the named dimensions. Package with
`iconutil -c icns PATH.iconset -o BatCave.icns`.

The bundle builders copy this ICNS resource. Updating source assets does not
replace the icon in an already installed app.
