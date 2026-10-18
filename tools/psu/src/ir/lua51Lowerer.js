import { rebuildConstantPool } from "./constantPool.js";
import { BINARY_OPCODE, LUA51_OPCODES as OP } from "./lua51Opcodes.js";

function abc(opcode, a = 0, b = 0, c = 0) {
  return { mode: "ABC", opcode, a, b, c };
}

function abx(opcode, a, bx) {
  return { mode: "ABx", opcode, a, bx };
}

function jump(opcode, target) {
  return { mode: "AsBx", opcode, a: 0, target };
}

function fieldName(instruction, property, fallback) {
  return instruction[property] ?? fallback;
}

function registerField(instruction, property, fallback) {
  return instruction[fieldName(instruction, property, fallback)];
}

function constantIndex(instruction, field, pool) {
  return pool.remap(instruction[field]);
}

function rk(instruction, field, pool) {
  return instruction[`constant${field.toUpperCase()}`]
    ? pool.rk(instruction[field])
    : instruction[field];
}

function callB(instruction) {
  if (instruction.args === "top") return 0;
  if (instruction.args === "none") return 1;
  if (instruction.args === "one") return 2;
  return Math.max(1, instruction.b - instruction.a + 1);
}

function callC(instruction) {
  if (instruction.results === "multi") return 0;
  if (instruction.results === "none") return 1;
  if (instruction.results === "one") return 2;
  return Math.max(1, instruction.c - instruction.a + 2);
}

function returnB(instruction) {
  if (instruction.mode === "top") return 0;
  if (instruction.mode === "none") return 1;
  if (instruction.mode === "one") return 2;
  if (instruction.mode === "two") return 3;
  return Math.max(1, instruction.b + 2);
}

function branchOpcode(operator) {
  switch (operator) {
    case "~=": return { opcode: OP.EQ, a: 0 };
    case "==": return { opcode: OP.EQ, a: 1 };
    case ">=": return { opcode: OP.LT, a: 0 };
    case "<": return { opcode: OP.LT, a: 1 };
    case ">": return { opcode: OP.LE, a: 0 };
    case "<=": return { opcode: OP.LE, a: 1 };
    default: throw new Error(`Unsupported PSU branch operator ${operator}`);
  }
}

function lowerOperation(instruction, chunk, pool) {
  const a = instruction.a;
  const b = instruction.b;
  const c = instruction.c;

  switch (instruction.kind) {
    case "nop":
      return [abc(OP.MOVE, 0, 0, 0)];
    case "loadk": {
      const source = fieldName(instruction, "sourceField", "b");
      return [
        abx(
          OP.LOADK,
          registerField(instruction, "destinationField", "a"),
          constantIndex(instruction, source, pool)
        )
      ];
    }
    case "move":
      return [
        abc(
          OP.MOVE,
          registerField(instruction, "destinationField", "a"),
          registerField(instruction, "sourceField", "b")
        )
      ];
    case "loadbool":
      return [abc(OP.LOADBOOL, a, b !== 0 ? 1 : 0, 0)];
    case "loadnil":
      return b >= a ? [abc(OP.LOADNIL, a, b, 0)] : [abc(OP.MOVE, 0, 0, 0)];
    case "getglobal": {
      const key = fieldName(instruction, "keyField", "b");
      return [
        abx(
          OP.GETGLOBAL,
          registerField(instruction, "destinationField", "a"),
          constantIndex(instruction, key, pool)
        )
      ];
    }
    case "setglobal":
      return [abx(OP.SETGLOBAL, a, constantIndex(instruction, "b", pool))];
    case "getupvalue":
      return [abc(OP.GETUPVAL, a, b, 0)];
    case "setupvalue":
      return [abc(OP.SETUPVAL, a, b, 0)];
    case "gettable":
      return [abc(OP.GETTABLE, a, b, rk(instruction, "c", pool))];
    case "settable":
      return [abc(OP.SETTABLE, a, rk(instruction, "b", pool), rk(instruction, "c", pool))];
    case "self":
      return [abc(OP.SELF, a, b, rk(instruction, "c", pool))];
    case "newtable":
      return [abc(OP.NEWTABLE, a, b, c)];
    case "binary":
      return [abc(BINARY_OPCODE[instruction.operator], a, rk(instruction, "b", pool), rk(instruction, "c", pool))];
    case "unm":
      return [abc(OP.UNM, a, b, 0)];
    case "not":
      return [abc(OP.NOT, a, b, 0)];
    case "len":
      return [abc(OP.LEN, a, b, 0)];
    case "concat":
      return [abc(OP.CONCAT, a, b, c)];
    case "jump":
      return [jump(OP.JMP, b)];
    case "branch": {
      const branch = branchOpcode(instruction.operator);
      return [
        abc(branch.opcode, branch.a, rk(instruction, instruction.left.field, pool), rk(instruction, instruction.right.field, pool)),
        jump(OP.JMP, b)
      ];
    }
    case "test":
      return [
        abc(OP.TEST, instruction[instruction.field], 0, instruction.truthy ? 1 : 0),
        jump(OP.JMP, b)
      ];
    case "testset":
      return [
        abc(OP.TESTSET, a, c, instruction.truthy ? 1 : 0),
        jump(OP.JMP, b)
      ];
    case "call":
      return [abc(OP.CALL, a, callB(instruction), callC(instruction))];
    case "tailcall":
      return [abc(OP.TAILCALL, a, callB(instruction), 0)];
    case "return":
      return [abc(OP.RETURN, a, returnB(instruction), 0)];
    case "closure": {
      if (b < 0 || b >= chunk.protos.length) {
        throw new Error("PSU closure references an invalid prototype");
      }
      const output = [abx(OP.CLOSURE, a, b)];
      for (const capture of instruction.captures ?? []) {
        output.push(
          capture.source === 0
            ? abc(OP.MOVE, 0, capture.index, 0)
            : abc(OP.GETUPVAL, 0, capture.index, 0)
        );
      }
      return output;
    }
    case "setlist":
      return [
        abc(
          OP.SETLIST,
          a,
          instruction.mode === "all" ? 0 : Math.max(0, b - a),
          c
        )
      ];
    case "vararg":
      return [abc(OP.VARARG, a, instruction.mode === "all" ? 0 : b, 0)];
    case "forprep":
      return [{ mode: "AsBx", opcode: OP.FORPREP, a, target: b }];
    case "forloop":
      return [{ mode: "AsBx", opcode: OP.FORLOOP, a, target: b }];
    case "tforloop":
      return [
        abc(OP.TFORLOOP, a, 0, c),
        jump(OP.JMP, b)
      ];
    default:
      throw new Error(`Unsupported PSU instruction kind ${instruction.kind}`);
  }
}

function isTerminal(instruction) {
  return instruction.kind === "jump" ||
    instruction.kind === "return" ||
    instruction.kind === "tailcall";
}

function inferUpvalueCounts(chunk, inherited = 0) {
  chunk.upvalueCount = inherited;
  for (const instruction of chunk.instructions) {
    if (instruction.kind !== "closure") continue;
    const child = chunk.protos[instruction.b];
    if (child) inferUpvalueCounts(child, instruction.captures?.length ?? 0);
  }
  for (const child of chunk.protos) {
    if (child.upvalueCount === undefined) inferUpvalueCounts(child, 0);
  }
}

function lowerChunk(chunk) {
  const pool = rebuildConstantPool(chunk);
  const groups = [];

  for (const instruction of chunk.instructions) {
    const output = lowerOperation(instruction, chunk, pool);
    const defaultTarget = instruction.jump ?? instruction.pc + 1;
    if (!isTerminal(instruction) && defaultTarget !== instruction.pc + 1) {
      output.push(jump(OP.JMP, defaultTarget));
    }
    groups.push({ pc: instruction.pc, output });
  }
  groups.push({
    pc: chunk.instructions.length,
    output: [abc(OP.RETURN, 0, 1, 0)]
  });

  const pcMap = new Map();
  let outputPc = 0;
  for (const group of groups) {
    pcMap.set(group.pc, outputPc);
    outputPc += group.output.length;
  }

  const instructions = [];
  for (const group of groups) {
    for (const instruction of group.output) {
      if (instruction.mode === "AsBx") {
        const target = pcMap.get(instruction.target);
        if (target === undefined) throw new Error("PSU control flow references an invalid instruction");
        instruction.sbx = target - (instructions.length + 1);
      }
      instructions.push(instruction);
    }
  }

  return {
    parameterCount: chunk.parameterCount,
    stackSize: requiredStackSize(instructions, chunk.parameterCount),
    upvalueCount: chunk.upvalueCount ?? 0,
    isVararg: instructions.some((instruction) => instruction.opcode === OP.VARARG),
    constants: pool.constants,
    instructions,
    protos: chunk.protos.map(lowerChunk)
  };
}

function requiredStackSize(instructions, parameterCount) {
  let maximum = Math.max(1, parameterCount - 1);
  const use = (register) => {
    if (Number.isInteger(register) && register >= 0 && register < 256) {
      maximum = Math.max(maximum, register);
    }
  };
  const useRk = (operand) => {
    if (operand < 256) use(operand);
  };

  for (const instruction of instructions) {
    const { opcode, a, b = 0, c = 0 } = instruction;
    switch (opcode) {
      case OP.MOVE:
        use(a); use(b);
        break;
      case OP.LOADK:
      case OP.LOADBOOL:
      case OP.GETGLOBAL:
      case OP.GETUPVAL:
      case OP.NEWTABLE:
      case OP.CLOSURE:
        use(a);
        break;
      case OP.LOADNIL:
        use(a); use(b);
        break;
      case OP.GETTABLE:
        use(a); use(b); useRk(c);
        break;
      case OP.SETGLOBAL:
      case OP.SETUPVAL:
        use(a);
        break;
      case OP.SETTABLE:
        use(a); useRk(b); useRk(c);
        break;
      case OP.SELF:
        use(a); use(a + 1); use(b); useRk(c);
        break;
      case OP.ADD:
      case OP.SUB:
      case OP.MUL:
      case OP.DIV:
      case OP.MOD:
      case OP.POW:
        use(a); useRk(b); useRk(c);
        break;
      case OP.UNM:
      case OP.NOT:
      case OP.LEN:
        use(a); use(b);
        break;
      case OP.CONCAT:
        use(a); use(b); use(c);
        break;
      case OP.EQ:
      case OP.LT:
      case OP.LE:
        useRk(b); useRk(c);
        break;
      case OP.TEST:
        use(a);
        break;
      case OP.TESTSET:
        use(a); use(b);
        break;
      case OP.CALL:
      case OP.TAILCALL:
        use(a);
        if (b > 0) use(a + b - 1);
        if (opcode === OP.CALL && c > 1) use(a + c - 2);
        break;
      case OP.RETURN:
        use(a);
        if (b > 1) use(a + b - 2);
        break;
      case OP.FORLOOP:
      case OP.FORPREP:
        use(a + 3);
        break;
      case OP.TFORLOOP:
        use(a + 2 + c);
        break;
      case OP.SETLIST:
        use(a);
        if (b > 0) use(a + b);
        break;
      case OP.VARARG:
        use(a);
        if (b > 1) use(a + b - 2);
        break;
    }
  }

  if (maximum >= 255) throw new Error("PSU prototype exceeds the Lua 5.1 register limit");
  return maximum + 1;
}

export function lowerToLua51(root) {
  inferUpvalueCounts(root, 0);
  return lowerChunk(root);
}
