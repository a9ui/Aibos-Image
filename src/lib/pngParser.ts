import fs from 'fs';

export interface ParsedMetadata {
  raw: string;
  prompt: string;
  negativePrompt: string;
  settings: Record<string, string>;
}

/**
 * Read only the PNG header chunks (up to IDAT) and extract the
 * tEXt chunk whose keyword is "parameters".
 *
 * This avoids reading the entire multi-MB image into memory.
 */
export function extractSDMetadata(filePath: string): ParsedMetadata | null {
  let fd: number | null = null;
  try {
    fd = fs.openSync(filePath, 'r');

    // Verify PNG signature (8 bytes)
    const sig = Buffer.alloc(8);
    fs.readSync(fd, sig, 0, 8, 0);
    if (sig.toString('hex') !== '89504e470d0a1a0a') {
      return null; // not a valid PNG
    }

    let offset = 8;
    const headerBuf = Buffer.alloc(8); // 4 bytes length + 4 bytes type

    while (true) {
      const bytesRead = fs.readSync(fd, headerBuf, 0, 8, offset);
      if (bytesRead < 8) break;

      const chunkLength = headerBuf.readUInt32BE(0);
      const chunkType = headerBuf.toString('ascii', 4, 8);

      // Stop once we hit image data – metadata always comes before IDAT
      if (chunkType === 'IDAT') break;

      if (chunkType === 'tEXt') {
        // Read the entire tEXt chunk data
        const dataBuf = Buffer.alloc(chunkLength);
        fs.readSync(fd, dataBuf, 0, chunkLength, offset + 8);

        // tEXt chunk: keyword\0text
        const nullIdx = dataBuf.indexOf(0);
        if (nullIdx >= 0) {
          const keyword = dataBuf.toString('ascii', 0, nullIdx);
          if (keyword === 'parameters') {
            const rawText = dataBuf.toString('utf-8', nullIdx + 1);
            fs.closeSync(fd);
            return parseSDText(rawText);
          }
        }
      }

      // Move to next chunk: 8 (header) + chunkLength (data) + 4 (CRC)
      offset += 8 + chunkLength + 4;
    }

    fs.closeSync(fd);
    return null;
  } catch {
    if (fd !== null) {
      try { fs.closeSync(fd); } catch { /* ignore */ }
    }
    return null;
  }
}

/**
 * Parse the raw SD "parameters" text into structured parts.
 *
 * Format:
 *   <prompt text, possibly multiline, may contain BREAK>
 *   Negative prompt: <negative prompt text, possibly multiline>
 *   Steps: <value>, Sampler: <value>, ...
 */
function parseSDText(raw: string): ParsedMetadata {
  let prompt = '';
  let negativePrompt = '';
  const settings: Record<string, string> = {};

  // Find "Negative prompt:" marker
  const negIdx = raw.indexOf('Negative prompt:');

  // Find the last line that starts with "Steps:" – this marks generation params
  const stepsMatch = raw.match(/\nSteps:\s/);
  const stepsIdx = stepsMatch ? raw.indexOf(stepsMatch[0]) : -1;

  if (negIdx >= 0 && stepsIdx >= 0) {
    prompt = raw.substring(0, negIdx).trim();
    negativePrompt = raw.substring(negIdx + 'Negative prompt:'.length, stepsIdx).trim();
    const settingsStr = raw.substring(stepsIdx + 1); // skip the \n
    parseSettingsLine(settingsStr, settings);
  } else if (negIdx >= 0) {
    prompt = raw.substring(0, negIdx).trim();
    negativePrompt = raw.substring(negIdx + 'Negative prompt:'.length).trim();
  } else if (stepsIdx >= 0) {
    prompt = raw.substring(0, stepsIdx).trim();
    const settingsStr = raw.substring(stepsIdx + 1);
    parseSettingsLine(settingsStr, settings);
  } else {
    prompt = raw.trim();
  }

  return { raw, prompt, negativePrompt, settings };
}

/**
 * Parse "Steps: 50, Sampler: DPM++ 3M SDE, ..." into key/value pairs.
 * Handles quoted values that may contain commas (e.g. ADetailer prompt).
 */
function parseSettingsLine(line: string, out: Record<string, string>) {
  // Simple state-machine parser for comma-separated key: value pairs
  // that respects quoted strings
  let current = line.trim();
  while (current.length > 0) {
    const colonIdx = current.indexOf(':');
    if (colonIdx < 0) break;

    const key = current.substring(0, colonIdx).trim();
    current = current.substring(colonIdx + 1).trimStart();

    let value: string;
    if (current.startsWith('"')) {
      // Find matching closing quote
      let endQuote = current.indexOf('"', 1);
      while (endQuote >= 0 && current[endQuote - 1] === '\\') {
        endQuote = current.indexOf('"', endQuote + 1);
      }
      if (endQuote >= 0) {
        value = current.substring(1, endQuote);
        current = current.substring(endQuote + 1).replace(/^\s*,\s*/, '');
      } else {
        value = current.substring(1);
        current = '';
      }
    } else {
      // Find next comma that's followed by a key (word + colon)
      const commaMatch = current.match(/,\s*(?=[A-Za-z_ ]+:)/);
      if (commaMatch && commaMatch.index !== undefined) {
        value = current.substring(0, commaMatch.index).trim();
        current = current.substring(commaMatch.index + 1).trimStart();
      } else {
        value = current.trim();
        current = '';
      }
    }

    out[key] = value;
  }
}
