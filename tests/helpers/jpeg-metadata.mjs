// Read camera JPEG dimensions and TIFF/EXIF tags using Node's built-in Buffer.
// This helper is used only to verify files produced by the Android smoke test.
export function jpegMetadata(bytes) {
  if (bytes.length < 4 || bytes.readUInt16BE(0) !== 0xffd8) throw new Error('Invalid JPEG');
  const result = { width: 0, height: 0, exif: {} };
  for (let offset = 2; offset < bytes.length;) {
    if (bytes[offset++] !== 0xff) throw new Error('Invalid JPEG marker');
    while (bytes[offset] === 0xff) offset++;
    const marker = bytes[offset++];
    if (marker === 0xda || marker === 0xd9) break;
    if (marker === 0x01 || (marker >= 0xd0 && marker <= 0xd7)) continue;
    if (offset + 2 > bytes.length) throw new Error('Truncated JPEG segment');
    const length = bytes.readUInt16BE(offset);
    if (length < 2 || offset + length > bytes.length) throw new Error('Invalid JPEG segment length');
    const data = bytes.subarray(offset + 2, offset + length);
    if ([0xc0, 0xc1, 0xc2, 0xc3, 0xc5, 0xc6, 0xc7, 0xc9, 0xca, 0xcb, 0xcd, 0xce, 0xcf].includes(marker)) {
      if (data.length < 6) throw new Error('Truncated JPEG frame');
      result.height = data.readUInt16BE(1); result.width = data.readUInt16BE(3);
    }
    if (marker === 0xe1 && data.subarray(0, 6).equals(Buffer.from('Exif\0\0'))) readExif(data.subarray(6), result.exif);
    offset += length;
  }
  if (!result.width || !result.height) throw new Error('Missing JPEG frame dimensions');
  return result;
}

function readExif(tiff, tags) {
  const order = tiff.subarray(0, 2).toString('ascii');
  if (order !== 'II' && order !== 'MM') throw new Error('Invalid EXIF byte order');
  const check = (offset, length) => {
    if (offset < 0 || length < 0 || offset + length > tiff.length) throw new Error('Invalid EXIF offset');
  };
  const u16 = offset => { check(offset, 2); return order === 'II' ? tiff.readUInt16LE(offset) : tiff.readUInt16BE(offset); };
  const u32 = offset => { check(offset, 4); return order === 'II' ? tiff.readUInt32LE(offset) : tiff.readUInt32BE(offset); };
  if (u16(2) !== 42) throw new Error('Invalid TIFF header');
  const visited = new Set();
  function directory(offset) {
    if (!offset || visited.has(offset)) return;
    visited.add(offset);
    const count = u16(offset); check(offset + 2, count * 12 + 4);
    for (let i = 0; i < count; i++) {
      const entry = offset + 2 + i * 12, tag = u16(entry), type = u16(entry + 2), count = u32(entry + 4);
      const size = ({ 1: 1, 2: 1, 3: 2, 4: 4, 5: 8 })[type];
      if (!size || !count) continue;
      const length = count * size, value = length <= 4 ? entry + 8 : u32(entry + 8);
      check(value, length);
      if (tag === 34665 && type === 4 && count === 1) { directory(u32(value)); continue; }
      if (type === 2) tags[tag] = tiff.subarray(value, value + length).toString('utf8').replace(/\0+$/, '');
      else if (type === 3) tags[tag] = String(u16(value));
      else if (type === 4) tags[tag] = String(u32(value));
      else if (type === 5) {
        const denominator = u32(value + 4);
        if (!denominator) throw new Error('Invalid EXIF rational');
        tags[tag] = String(u32(value) / denominator);
      } else tags[tag] = String(tiff[value]);
    }
  }
  directory(u32(4));
}
