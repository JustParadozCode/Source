function writeByte(parts, value) {
  const buffer = Buffer.allocUnsafe(1);
  buffer.writeUInt8(value & 0xff);
  parts.push(buffer);
}

function writeInt32(parts, value) {
  const buffer = Buffer.allocUnsafe(4);
  buffer.writeInt32LE(value);
  parts.push(buffer);
}

function writeUInt32(parts, value) {
  const buffer = Buffer.allocUnsafe(4);
  buffer.writeUInt32LE(value >>> 0);
  parts.push(buffer);
}

function writeSize(parts, value) {
  const buffer = Buffer.alloc(8);
  buffer.writeBigUInt64LE(BigInt(value));
  parts.push(buffer);
}

function writeNumber(parts, value) {
  const buffer = Buffer.allocUnsafe(8);
  buffer.writeDoubleLE(value);
  parts.push(buffer);
}

function writeString(parts, value) {
  if (value === null) {
    writeSize(parts, 0);
    return;
  }
  const bytes = Buffer.isBuffer(value) ? value : Buffer.from(value, "latin1");
  writeSize(parts, bytes.length + 1);
  parts.push(bytes);
  writeByte(parts, 0);
}

function encodeInstruction(instruction) {
  const opcode = instruction.opcode & 0x3f;
  const a = instruction.a & 0xff;
  if (instruction.mode === "ABC") {
    return (
      opcode |
      (a << 6) |
      ((instruction.c & 0x1ff) << 14) |
      ((instruction.b & 0x1ff) << 23)
    ) >>> 0;
  }
  const bx = instruction.mode === "AsBx"
    ? instruction.sbx + 131071
    : instruction.bx;
  return (opcode | (a << 6) | ((bx & 0x3ffff) << 14)) >>> 0;
}

function writeConstant(parts, value) {
  if (value === null || value === undefined) {
    writeByte(parts, 0);
  } else if (typeof value === "boolean") {
    writeByte(parts, 1);
    writeByte(parts, value ? 1 : 0);
  } else if (typeof value === "number") {
    writeByte(parts, 3);
    writeNumber(parts, value);
  } else {
    writeByte(parts, 4);
    writeString(parts, String(value));
  }
}

function writeChunk(parts, chunk, sourceName, includeSource) {
  writeString(parts, includeSource ? sourceName : null);
  writeInt32(parts, 0);
  writeInt32(parts, 0);
  writeByte(parts, chunk.upvalueCount);
  writeByte(parts, chunk.parameterCount);
  writeByte(parts, chunk.isVararg ? 2 : 0);
  writeByte(parts, chunk.stackSize);

  writeInt32(parts, chunk.instructions.length);
  for (const instruction of chunk.instructions) {
    writeUInt32(parts, encodeInstruction(instruction));
  }

  writeInt32(parts, chunk.constants.length);
  for (const constant of chunk.constants) writeConstant(parts, constant);

  writeInt32(parts, chunk.protos.length);
  for (const proto of chunk.protos) writeChunk(parts, proto, sourceName, false);

  writeInt32(parts, 0);
  writeInt32(parts, 0);
  writeInt32(parts, 0);
}

export function writeLua51Chunk(root, sourceName = "@psu") {
  const parts = [
    Buffer.from([
      0x1b, 0x4c, 0x75, 0x61,
      0x51,
      0,
      1,
      4,
      8,
      4,
      8,
      0
    ])
  ];
  writeChunk(parts, root, sourceName, true);
  return Buffer.concat(parts);
}
