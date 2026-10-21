// Development only: Node.js + sharp. The application consumes the committed ICOs.
const fs = require('node:fs/promises');
const path = require('node:path');
const sharp = require('sharp');
const root = path.resolve(__dirname, '..');
const assets = path.join(root, 'Assets');

async function render(svg, size) {
  return sharp(svg, { density: 384 }).resize(size, size).png().toBuffer();
}
function encodeIco(frames) {
  const directory = Buffer.alloc(6 + frames.length * 16);
  directory.writeUInt16LE(1, 2);
  directory.writeUInt16LE(frames.length, 4);
  let offset = directory.length;
  frames.forEach(({ size, png }, i) => {
    const entry = 6 + i * 16;
    directory[entry] = size === 256 ? 0 : size;
    directory[entry + 1] = size === 256 ? 0 : size;
    directory.writeUInt16LE(1, entry + 4);
    directory.writeUInt16LE(32, entry + 6);
    directory.writeUInt32LE(png.length, entry + 8);
    directory.writeUInt32LE(offset, entry + 12);
    offset += png.length;
  });
  return Buffer.concat([directory, ...frames.map(f => f.png)]);
}

async function main() {
  const app = await fs.readFile(path.join(assets, 'cut.svg'));
  const tray = await fs.readFile(path.join(assets, 'cut-tray.svg'));
  for (const [name, sizes, smallOnly] of [
    ['Cut.ico', [16, 20, 24, 32, 40, 48, 64, 128, 256], false],
    ['Cut.Tray.ico', [16, 20, 24, 32, 40, 48, 64], true]
  ]) {
    const frames = [];
    for (const size of sizes) frames.push({ size, png: await render(smallOnly || size <= 32 ? tray : app, size) });
    await fs.writeFile(path.join(assets, name), encodeIco(frames));
    console.log(`${name}: ${sizes.join(', ')} px`);
  }
  const output = path.join(root, 'artifacts', 'icons');
  await fs.mkdir(output, { recursive: true });
  await fs.writeFile(path.join(output, 'cut-preview.png'), await render(app, 256));
  const layers = [];
  const sizes = [16, 20, 24, 32, 48, 64];
  for (const [row, top] of [[0, 48], [1, 172]]) {
    for (let i = 0; i < sizes.length; i++) {
      const size = sizes[i];
      layers.push({ input: await render(tray, size), left: 32 + i * 88 + Math.floor((64 - size) / 2), top: top + Math.floor((64 - size) / 2) });
    }
  }
  const background = Buffer.from(`<svg xmlns="http://www.w3.org/2000/svg" width="592" height="280"><rect width="592" height="140" fill="#F5F7FB"/><rect y="140" width="592" height="140" fill="#18222F"/><g font-family="sans-serif" font-size="12">${sizes.map((s, i) => `<text x="${64 + i * 88}" y="28" text-anchor="middle" fill="#405369">${s}px</text><text x="${64 + i * 88}" y="264" text-anchor="middle" fill="#D5E0EC">${s}px</text>`).join('')}</g></svg>`);
  await sharp(background).composite(layers).png().toFile(path.join(output, 'tray-sizes.png'));
}
main().catch(error => { console.error(error); process.exitCode = 1; });
