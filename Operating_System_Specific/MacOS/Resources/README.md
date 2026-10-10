# Mac app icon

`BatCave.png` is the transparent 1024-pixel source for `BatCave.icns`.
The corrected outer edge has no opaque white outline or corner artifacts.
Keep the intentional gray highlights inside the artwork.

To rebuild, resize the PNG with `sips -z SIZE SIZE` into a `.iconset` directory
with these eight modern PNG-backed representations, matching the original icon:

| Base name | Variant suffix | Pixels |
| --- | --- | --- |
| icon_16x16 | `@2x` | 32 |
| icon_32x32 | `@2x` | 64 |
| icon_128x128 | none | 128 |
| icon_128x128 | `@2x` | 256 |
| icon_256x256 | none | 256 |
| icon_256x256 | `@2x` | 512 |
| icon_512x512 | none | 512 |
| icon_512x512 | `@2x` | 1024 |

Append the variant suffix and `.png` to each base name.

Package with `iconutil -c icns PATH.iconset -o BatCave.icns`. Do not add the
non-Retina 16px/32px records: Apple's legacy RGB conversion can recreate a pale
edge. macOS downsamples the modern representations for those small sizes.
Verify every extracted PNG's RGBA pixels against its source after packaging.

The bundle builders copy this ICNS resource. Updating source assets does not
replace the icon in an already installed app.
