# Mail Concierge brand

The mascot is a butler holding an envelope on a tray: the plugin hands out invitations and recovery e-mails on the server's behalf.

## Files

| File | Use |
|---|---|
| `logo-1024.png` … `logo-32.png` | App icon: the butler on the rounded gradient badge, transparent outside the badge. `logo-512.png` is the reference size. |
| `favicon.ico` | 16, 32, 48 and 64 px. |
| `mascot-1024.png`, `mascot-512.png` | The butler alone, transparent background, for large layouts. |
| `glyph-white-*.png`, `glyph-navy-*.png` | Single-colour butler, transparent background: white on dark backgrounds, navy on light ones. Use it below 32 px, in e-mails and in print. |
| `catalog-thumb.svg` | Vector 16:9 image (1280x720 viewBox, the name is converted to outlines) for the Jellyfin plugin catalog (`imageUrl` of the manifest): the white butler and the white name on a dark gradient, like the other catalog cards, without the badge. |
| `social-preview-1280x640.png` | GitHub social preview (Settings > General > Social preview): white butler and name on near-black, a faint pink halo on the left edge and a blue one on the right, with the tagline. |
| `form-logo-*.png` | White butler on the gradient rounded triangle, transparent outside; shown at the top of the public pages (dark background only — the white figure disappears on light backgrounds). Embedded as `Resources/Pages/form-logo.png` (192 px). |
| `source/` | The generated originals, with their magenta keying background. |

The e-mails use `logo-128.png`, embedded as `Jellyfin.Plugin.MailConcierge/Resources/Pages/logo.png`.

## Colours

| Name | Hex | Use |
|---|---|---|
| Pink | `#F040A8` | Gradient start (top-left) |
| Purple | `#AA5CC3` | Gradient middle, envelope fold lines |
| Blue | `#00A4DC` | Gradient end (bottom-right) |
| Navy | `#0E1220` | Butler silhouette |
| Dark | `#101010` | Dark backgrounds |
| White | `#FFFFFF` | Shirt, glove, towel, envelope |

Purple and blue are Jellyfin's own colours, so the plugin sits well in its interface.

## Type

Titles use [Quicksand](https://fonts.google.com/specimen/Quicksand) (SIL Open Font License), bold for the name, medium for the tagline.

## Do not

- Do not use the Jellyfin triangle logo in Mail Concierge artwork: the plugin is not an official Jellyfin project.
- Do not recolour, stretch or crop the badge, or add effects to it.
- Do not put text inside the icon.
