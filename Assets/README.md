# Cut icon

`Toolbar/` contains the original toolbar SVG artwork (24 x 24 viewBox, 1.8-unit strokes). Shapes are authored as SVG paths and embedded as WPF resources. `Services/ToolbarIcons.cs` reads this limited path-only SVG format into frozen WPF vector drawings; rendering remains vector-based at all DPI scales. Buttons use 36 x 36 DIP targets and 20 x 20 DIP drawings, retain text tooltips and automation names, and share the same artwork across screenshot and long-image editors. No SVG rendering package or raster conversion is required. Keep new icons within this supported SVG subset.

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
