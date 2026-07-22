import fs from 'fs';
import path from 'path';

function readSDMetadata(filePath) {
  const buffer = fs.readFileSync(filePath);
  if (buffer.toString('ascii', 0, 8) !== '\x89PNG\r\n\x1a\n') {
    console.log("Not a valid PNG file.");
    return;
  }

  let offset = 8;
  while (offset < buffer.length) {
    const length = buffer.readUInt32BE(offset);
    const type = buffer.toString('ascii', offset + 4, offset + 8);
    const dataOffset = offset + 8;

    if (type === 'tEXt') {
      const keywordNullParams = buffer.indexOf(0, dataOffset);
      const keyword = buffer.toString('utf-8', dataOffset, keywordNullParams);
      if (keyword === 'parameters') {
        const text = buffer.toString('utf-8', keywordNullParams + 1, dataOffset + length);
        console.log('--- METADATA FOUND IN:', path.basename(filePath), '---');
        console.log(text);
        return text;
      }
    }
    offset = dataOffset + length + 4; // length + CRC
  }
  console.log("No SD metadata 'parameters' found.");
}

const arg = process.argv[2];
if (!arg) {
  console.error('Usage: node scripts/dump_metadata.mjs <image.png>');
  process.exit(2);
}

readSDMetadata(path.resolve(arg));
