# Cut icon

The canonical artwork is SVG, drawn as simple geometric paths. No generated bitmap is used.

- `cut.svg`: application mark, a teal tile, white selection corners and mint captured region.
- `cut-tray.svg`: optical variant for small sizes, with stronger two-pixel corner arms at 16px.
- `Cut.ico`: Windows executable/window icon, with 16/20/24/32/40/48/64/128/256px frames.
- `Cut.Tray.ico`: notification-area icon, with 16/20/24/32/40/48/64px frames.

Both ICOs retain transparency and are embedded in the application; no loose files are required at runtime. Small application frames use the tray artwork for legibility.

To regenerate after editing the SVGs, install Node.js and `sharp`, then run:

```powershell
npm install --no-save --package-lock=false sharp
node Tools/build-icons.cjs
```

The generator also writes a light/dark small-size comparison to `artifacts/icons/tray-sizes.png`. Node.js and sharp are only needed to regenerate artwork, not to build or run Cut.
