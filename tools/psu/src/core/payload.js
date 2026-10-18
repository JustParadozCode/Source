function extractMarkedPayload(source) {
  const candidates = [];
  let marker = 0;
  while ((marker = source.indexOf("PSU|", marker)) >= 0) {
    const start = marker + 4;
    const longEnd = source.indexOf("]=]", start);
    const quoteEnd = source.indexOf('"', start);
    const end = longEnd >= 0 && (quoteEnd < 0 || longEnd < quoteEnd) ? longEnd : quoteEnd;
    if (end >= 0) candidates.push(source.slice(start, end));
    marker = start;
  }

  if (candidates.length === 0) {
    throw new Error("Input does not contain a valid PSU payload");
  }
  candidates.sort((left, right) => right.length - left.length);
  return candidates[0];
}

function decodeBase36Stream(encoded) {
  const codes = [];
  let position = 0;

  while (position < encoded.length) {
    const width = Number.parseInt(encoded[position], 36);
    position += 1;
    if (!Number.isInteger(width) || width < 1 || position + width > encoded.length) {
      throw new Error("Invalid PSU compressed stream");
    }

    const value = Number.parseInt(encoded.slice(position, position + width), 36);
    position += width;
    if (!Number.isSafeInteger(value) || value < 0) {
      throw new Error("Invalid PSU compressed code");
    }
    codes.push(value);
  }

  return codes;
}

function decompressLzw(codes) {
  if (codes.length === 0 || codes[0] > 255) {
    throw new Error("Invalid PSU LZW stream");
  }

  const dictionary = Array.from({ length: 256 }, (_, index) =>
    String.fromCharCode(index)
  );
  let previous = dictionary[codes[0]];
  const parts = [previous];

  for (let index = 1; index < codes.length; index += 1) {
    const code = codes[index];
    const entry = dictionary[code] ??
      (code === dictionary.length ? previous + previous[0] : null);
    if (entry === null) {
      throw new Error("Invalid PSU LZW dictionary reference");
    }

    parts.push(entry);
    dictionary.push(previous + entry[0]);
    previous = entry;
  }

  return Buffer.from(parts.join(""), "latin1");
}

export function decodePayload(source) {
  const encoded = extractMarkedPayload(source);
  if (!/^[0-9A-Za-z]+$/.test(encoded)) {
    throw new Error("Unsupported PSU bytecode alphabet");
  }
  return decompressLzw(decodeBase36Stream(encoded));
}
