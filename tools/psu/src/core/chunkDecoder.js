const CHUNK_FIELDS = ["parameters", "instructions", "protos", "stack"];
const INSTRUCTION_FIELDS = ["op", "a", "b", "c"];

function permutations(values) {
  if (values.length <= 1) return [values.slice()];
  const result = [];
  for (let index = 0; index < values.length; index += 1) {
    const rest = values.slice(0, index).concat(values.slice(index + 1));
    for (const tail of permutations(rest)) result.push([values[index], ...tail]);
  }
  return result;
}

const CHUNK_ORDERS = permutations(CHUNK_FIELDS);
const INSTRUCTION_ORDERS = permutations(INSTRUCTION_FIELDS);

class Reader {
  constructor(buffer, key, position = 0) {
    this.buffer = buffer;
    this.position = position;
    this.key = key;
  }

  ensure(length) {
    if (this.position + length > this.buffer.length) {
      throw new Error("PSU chunk is truncated");
    }
  }

  byte() {
    this.ensure(1);
    const value = this.buffer[this.position++] ^ this.key;
    this.key = value & 0xff;
    return value;
  }

  bytes(length) {
    this.ensure(length);
    const output = Buffer.allocUnsafe(length);
    for (let index = 0; index < length; index += 1) output[index] = this.byte();
    return output;
  }

  raw(length) {
    this.ensure(length);
    const output = this.buffer.subarray(this.position, this.position + length);
    this.position += length;
    return output;
  }

  uint16() {
    return this.bytes(2).readUInt16LE(0);
  }

  int16() {
    return this.bytes(2).readInt16LE(0);
  }

  uint32() {
    return this.bytes(4).readUInt32LE(0);
  }

  int32() {
    return this.bytes(4).readInt32LE(0);
  }

  number() {
    return this.bytes(8).readDoubleLE(0);
  }
}

function saneCount(value, maximum, label) {
  if (!Number.isInteger(value) || value < 0 || value > maximum) {
    throw new Error(`Invalid PSU ${label}`);
  }
  return value;
}

function decodeConstants(reader, tags) {
  const count = saneCount(reader.uint32(), 1_000_000, "constant count");
  const constants = [];

  for (let index = 0; index < count; index += 1) {
    const tag = reader.byte();
    const comparableTag = tags.modulo ? tag % tags.modulo : tag;
    if (comparableTag === tags.boolean) {
      constants.push(reader.byte() !== 0);
    } else if (comparableTag === tags.number) {
      const value = reader.number();
      if (!Number.isFinite(value) && !Number.isNaN(value)) {
        constants.push(value);
      } else if (Math.abs(value) > Number.MAX_VALUE) {
        throw new Error("Invalid PSU number");
      } else {
        constants.push(value);
      }
    } else if (
      comparableTag === tags.string ||
      (tags.fastString !== null && comparableTag === tags.fastString)
    ) {
      const length = saneCount(reader.uint32(), reader.buffer.length, "string length");
      const bytes = tags.fastString !== null && comparableTag === tags.fastString
        ? reader.raw(length)
        : reader.bytes(length);
      constants.push(bytes.toString("latin1"));
    } else {
      constants.push(null);
    }
  }

  return constants;
}

function decodeInstruction(reader, order, constants, index, count) {
  const encodedData = reader.byte();
  if (encodedData === 0) {
    return { pc: index, skipped: true, op: 0, a: 0, b: 0, c: 0 };
  }

  const data = encodedData - 1;
  const type = data & 7;
  if (![0, 1, 2, 3, 5, 6].includes(type)) {
    throw new Error("Invalid PSU instruction type");
  }

  const instruction = {
    pc: index,
    type,
    flags: data,
    op: 0,
    a: 0,
    b: 0,
    c: 0,
    constantA: (data & 8) !== 0,
    constantB: (data & 16) !== 0,
    constantC: (data & 32) !== 0
  };

  for (const field of order) {
    if (field === "op") {
      instruction.op = reader.byte();
    } else if (field === "a" && type !== 6) {
      instruction.a = reader.int16();
    } else if (field === "b" && type !== 6) {
      instruction.b = type === 0 ? reader.int16() : reader.int32();
    } else if (field === "c" && [0, 3, 5].includes(type)) {
      instruction.c = reader.int16();
    }
  }

  if (type === 5) {
    saneCount(instruction.c, 255, "closure upvalue count");
    instruction.captures = [];
    for (let capture = 0; capture < instruction.c; capture += 1) {
      instruction.captures.push({
        source: reader.byte(),
        index: reader.int16()
      });
    }
  }

  if ((data & 128) !== 0) {
    instruction.jump = saneCount(reader.uint32(), count, "jump target");
  }

  if ((data & 64) !== 0) {
    const customCount = saneCount(reader.byte(), 255, "custom data count");
    instruction.custom = [];
    for (let custom = 0; custom < customCount; custom += 1) {
      instruction.custom.push(reader.int32());
    }
  }

  for (const field of ["A", "B", "C"]) {
    if (!instruction[`constant${field}`]) continue;
    const index = instruction[field.toLowerCase()];
    saneCount(index, Math.max(0, constants.length - 1), "constant index");
    instruction[`value${field}`] = constants[index];
  }
  return instruction;
}

function decodeInstructions(reader, order, constants) {
  const count = saneCount(reader.uint32(), 2_000_000, "instruction count");
  const instructions = [];
  for (let index = 0; index < count; index += 1) {
    instructions.push(decodeInstruction(reader, order, constants, index, count));
  }
  for (const instruction of instructions) {
    if (!instruction.skipped && [2, 3].includes(instruction.type)) {
      saneCount(instruction.b, Math.max(0, count - 1), "branch target");
    }
  }
  return instructions;
}

function decodeChunk(reader, chunkOrder, instructionOrder, tags, depth = 0) {
  if (depth > 200) throw new Error("PSU prototype nesting is too deep");
  const chunk = {
    parameterCount: null,
    stackSize: null,
    constants: [],
    instructions: [],
    protos: []
  };

  for (const field of chunkOrder) {
    if (field === "parameters") {
      chunk.parameterCount = reader.byte();
    } else if (field === "stack") {
      chunk.stackSize = saneCount(reader.uint16(), 65_535, "stack size");
    } else if (field === "instructions") {
      chunk.constants = decodeConstants(reader, tags);
      const start = reader.position;
      const key = reader.key;
      chunk.instructions = decodeInstructions(reader, instructionOrder, chunk.constants);
      chunk.instructionRegion = {
        start,
        end: reader.position,
        key,
        order: instructionOrder
      };
    } else if (field === "protos") {
      const count = saneCount(reader.uint32(), 100_000, "prototype count");
      for (let index = 0; index < count; index += 1) {
        chunk.protos.push(
          decodeChunk(reader, chunkOrder, instructionOrder, tags, depth + 1)
        );
      }
    }
  }

  if (chunk.parameterCount === null || chunk.stackSize === null) {
    throw new Error("Incomplete PSU chunk");
  }
  if (chunk.parameterCount > chunk.stackSize + 32) {
    throw new Error("Invalid PSU parameter count");
  }
  return chunk;
}

function scoreChunk(chunk) {
  let score = chunk.instructions.length > 0 ? 1_000 : 0;
  const registerLimit = Math.max(chunk.stackSize + 8, 16);
  const operandLimit = Math.max(registerLimit, chunk.constants.length + 8);

  if (chunk.stackSize > 255) score -= (chunk.stackSize - 255) * 10_000;
  if (chunk.parameterCount > chunk.stackSize) score -= 100_000;

  for (const instruction of chunk.instructions) {
    if (instruction.skipped) {
      score += 1;
      continue;
    }

    score += 100;
    if (!instruction.constantA) {
      if (instruction.a < 0) score -= 100_000;
      if (instruction.a > registerLimit) {
        score -= 10_000 + (instruction.a - registerLimit);
      }
    }

    if (instruction.type === 0) {
      for (const field of ["b", "c"]) {
        const upper = field.toUpperCase();
        if (instruction[`constant${upper}`]) continue;
        const value = instruction[field];
        if (value < 0) score -= 100_000;
        if (value > operandLimit) score -= 1_000 + (value - operandLimit);
      }
    }

    if (instruction.type === 5) {
      if (instruction.b < 0 || instruction.b >= chunk.protos.length) {
        score -= 1_000_000;
      }
      for (const capture of instruction.captures ?? []) {
        if (![0, 1].includes(capture.source) || capture.index < 0) {
          score -= 100_000;
        }
      }
    }
  }

  for (const child of chunk.protos) score += scoreChunk(child);
  return score;
}

function refineInstructionOrders(buffer, chunk, semanticScore) {
  let changed = 0;
  for (const child of chunk.protos) {
    changed += refineInstructionOrders(buffer, child, semanticScore);
  }

  const region = chunk.instructionRegion;
  if (!region) return changed;

  let bestInstructions = chunk.instructions;
  let bestOrder = region.order;
  let bestScore = Number.NEGATIVE_INFINITY;
  let originalScore = Number.NEGATIVE_INFINITY;

  for (const order of INSTRUCTION_ORDERS) {
    const reader = new Reader(buffer, region.key, region.start);
    try {
      const instructions = decodeInstructions(reader, order, chunk.constants);
      if (reader.position !== region.end) continue;

      const candidate = { ...chunk, instructions };
      let semantics = 0;
      if (semanticScore) {
        try {
          semantics = semanticScore(candidate);
        } catch {
          semantics = -1_000_000_000;
        }
      }
      const score = scoreChunk(candidate) + semantics;
      if (order.join("|") === region.order.join("|")) originalScore = score;
      if (score > bestScore) {
        bestScore = score;
        bestInstructions = instructions;
        bestOrder = order;
      }
    } catch {
      continue;
    }
  }

  const shouldRefine =
    bestOrder.join("|") !== region.order.join("|") &&
    bestScore >= originalScore + 100_000;

  if (shouldRefine) {
    chunk.instructions = bestInstructions;
    region.order = bestOrder;
    changed += 1;
  }
  return changed;
}

export function decodeChunkPayload(buffer, tags, semanticScore = null) {
  const candidates = [];

  for (let initialKey = 0; initialKey < 256; initialKey += 1) {
    for (const chunkOrder of CHUNK_ORDERS) {
      for (const instructionOrder of INSTRUCTION_ORDERS) {
        const reader = new Reader(buffer, initialKey);
        try {
          const chunk = decodeChunk(reader, chunkOrder, instructionOrder, tags);
          if (reader.position !== buffer.length) continue;
          let semantics = 0;
          if (semanticScore) {
            try {
              semantics = semanticScore(chunk);
            } catch {
              semantics = -1_000_000_000;
            }
          }
          candidates.push({
            chunk,
            initialKey,
            chunkOrder,
            instructionOrder,
            score: scoreChunk(chunk) + semantics
          });
        } catch {
        }
      }
    }
  }

  if (candidates.length === 0) {
    throw new Error("Unsupported PSU chunk layout");
  }

  candidates.sort((left, right) => right.score - left.score);
  const best = candidates[0];
  const refinedInstructionOrders = refineInstructionOrders(
    buffer,
    best.chunk,
    semanticScore
  );
  return {
    ...best,
    candidateCount: candidates.length,
    refinedInstructionOrders
  };
}
