function constantKey(value) {
  if (value === null || value === undefined) return "nil";
  if (typeof value === "number") {
    if (Number.isNaN(value)) return "number:nan";
    if (Object.is(value, -0)) return "number:-0";
  }
  return `${typeof value}:${String(value)}`;
}

function constantFields(instruction) {
  const fields = [];
  for (const field of ["a", "b", "c"]) {
    if (instruction[`constant${field.toUpperCase()}`]) fields.push(field);
  }
  return fields;
}

function usesRk(instruction, field) {
  if (!instruction[`constant${field.toUpperCase()}`]) return false;
  if (instruction.kind === "branch") return field === "a" || field === "c";
  if (instruction.kind === "binary") return field === "b" || field === "c";
  if (instruction.kind === "gettable" || instruction.kind === "self") return field === "c";
  if (instruction.kind === "settable") return field === "b" || field === "c";
  return false;
}

export function rebuildConstantPool(chunk) {
  const orderedOldIndexes = [];
  const seenOldIndexes = new Set();

  function add(index, instruction = null, field = null) {
    if (!Number.isInteger(index) || index < 0 || index >= chunk.constants.length) {
      const location = instruction
        ? ` at ${chunk.path ?? "chunk"}:${instruction.pc} (${instruction.kind}.${field})`
        : "";
      throw new Error(`PSU instruction references an invalid constant${location}`);
    }
    if (!seenOldIndexes.has(index)) {
      seenOldIndexes.add(index);
      orderedOldIndexes.push(index);
    }
  }

  for (const instruction of chunk.instructions) {
    for (const field of constantFields(instruction)) {
      if (usesRk(instruction, field)) add(instruction[field], instruction, field);
    }
  }
  if (orderedOldIndexes.length > 256) {
    throw new Error("PSU prototype uses more than 256 RK constants");
  }
  for (let index = 0; index < chunk.constants.length; index += 1) add(index);

  const constants = [];
  const valueMap = new Map();
  const indexMap = new Map();
  for (const oldIndex of orderedOldIndexes) {
    const value = chunk.constants[oldIndex];
    const key = constantKey(value);
    let newIndex = valueMap.get(key);
    if (newIndex === undefined) {
      newIndex = constants.length;
      constants.push(value);
      valueMap.set(key, newIndex);
    }
    indexMap.set(oldIndex, newIndex);
  }

  return {
    constants,
    remap(oldIndex) {
      const index = indexMap.get(oldIndex);
      if (index === undefined) throw new Error("PSU constant remapping failed");
      return index;
    },
    rk(oldIndex) {
      const index = this.remap(oldIndex);
      if (index > 255) throw new Error("PSU RK constant is outside the Lua 5.1 range");
      return index + 256;
    }
  };
}
