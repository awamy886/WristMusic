// Original vector-style waveform, encoded as a 64px uncompressed Windows icon.
import { writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
const n = 64, image = Buffer.alloc(40 + n * n * 4 + n * 8);
image.writeUInt32LE(40, 0); image.writeInt32LE(n, 4); image.writeInt32LE(n * 2, 8);
image.writeUInt16LE(1, 12); image.writeUInt16LE(32, 14); image.writeUInt32LE(n * n * 4, 20);
const bars = [{x:12,h:14},{x:21,h:30},{x:30,h:44},{x:39,h:26},{x:48,h:12}];
for (let y = 0; y < n; y++) for (let x = 0; x < n; x++) {
  const corner = Math.hypot(Math.max(12 - x, x - 51, 0), Math.max(12 - y, y - 51, 0)) <= 12;
  let rgba = corner ? [25, 24, 35, 255] : [0, 0, 0, 0];
  if (bars.some(b => Math.abs(x - b.x) <= 2 && Math.abs(y - 32) <= b.h / 2)) rgba = [185, 153, 255, 255];
  const p = 40 + ((n - 1 - y) * n + x) * 4;
  image[p] = rgba[2]; image[p + 1] = rgba[1]; image[p + 2] = rgba[0]; image[p + 3] = rgba[3];
}
const header = Buffer.alloc(22); header.writeUInt16LE(1, 2); header.writeUInt16LE(1, 4);
header[6] = n; header[7] = n; header.writeUInt16LE(1, 10); header.writeUInt16LE(32, 12);
header.writeUInt32LE(image.length, 14); header.writeUInt32LE(22, 18);
writeFileSync(fileURLToPath(new URL('../src/WristMusic/Assets/app.ico', import.meta.url)), Buffer.concat([header, image]));
