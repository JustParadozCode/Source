import { recoverUnknownInstruction } from "../ir/semanticRecovery.js";

const FIELD_TOKEN = String.raw`I\[(?:"(?:[^"\\]|\\.)*"|-?\d+(?:\.\d+)?)\]`;

function fieldKey(token) {
  return token.slice(2, -1);
}

function recoverFieldKeys(handlers) {
  const destinationCounts = new Map();
  const jumpCounts = new Map();
  const allCounts = new Map();
  const destinationPattern = new RegExp(String.raw`R\[(I\[(?:"(?:[^"\\]|\\.)*"|-?\d+(?:\.\d+)?)\])\]=`, "g");
  const jumpPattern = new RegExp(String.raw`IP=(I\[(?:"(?:[^"\\]|\\.)*"|-?\d+(?:\.\d+)?)\])`, "g");
  const allPattern = new RegExp(`(${FIELD_TOKEN})`, "g");

  for (const handler of handlers.values()) {
    for (const segment of handler.segments) {
      for (const match of segment.matchAll(allPattern)) {
        const key = fieldKey(match[1]);
        allCounts.set(key, (allCounts.get(key) ?? 0) + 1);
      }
      for (const match of segment.matchAll(destinationPattern)) {
        const key = fieldKey(match[1]);
        destinationCounts.set(key, (destinationCounts.get(key) ?? 0) + 1);
      }
      for (const match of segment.matchAll(jumpPattern)) {
        const key = fieldKey(match[1]);
        jumpCounts.set(key, (jumpCounts.get(key) ?? 0) + 1);
      }
    }
  }

  const a = [...destinationCounts].sort((left, right) => right[1] - left[1])[0]?.[0];
  let b = [...jumpCounts].sort((left, right) => right[1] - left[1])[0]?.[0];
  if (!b) {
    for (const handler of handlers.values()) {
      for (const segment of handler.segments) {
        const match = /^R\[I\[([^\]]+)\]\]=I\[([^\]]+)\]$/.exec(segment);
        if (match && match[1] === a) {
          b = match[2];
          break;
        }
      }
      if (b) break;
    }
  }
  const c = [...allCounts]
    .filter(([key]) => key !== a && key !== b)
    .sort((left, right) => right[1] - left[1])[0]?.[0];

  if (a === undefined || b === undefined || c === undefined) {
    throw new Error("Unsupported PSU instruction field map");
  }
  return { a, b, c };
}

function replaceFields(text, fields) {
  let result = text;
  for (const [name, key] of Object.entries(fields)) {
    result = result.split(`I[${key}]`).join(`I.${name.toUpperCase()}`);
  }
  return result;
}

function recoverConstantAlias(handlers, fields) {
  const counts = new Map();
  for (const handler of handlers.values()) {
    for (const segment of handler.segments) {
      const normalized = replaceFields(segment, fields);
      if (normalized.includes("=WRAP(")) continue;
      for (const match of normalized.matchAll(/\b(v_[A-Za-z0-9_]+)\[I\.[ABC]\]/g)) {
        counts.set(match[1], (counts.get(match[1]) ?? 0) + 1);
      }
    }
  }
  return [...counts].sort((left, right) => right[1] - left[1])[0]?.[0] ?? null;
}

function operandDescriptor(text) {
  const register = /^R\[I\.([ABC])\]$/.exec(text);
  if (register) return { type: "register", field: register[1].toLowerCase() };
  const immediate = /^I\.([ABC])$/.exec(text);
  if (immediate) return { type: "immediate", field: immediate[1].toLowerCase() };
  return null;
}

function classifyBranch(text) {
  let match = /^if \((R\[I\.[ABC]\]|I\.[ABC])((?:==|~=|<=|>=|<|>))(R\[I\.[ABC]\]|I\.[ABC])\) then IP=I\.B end$/.exec(text);
  if (match) {
    return {
      kind: "branch",
      operator: match[2],
      left: operandDescriptor(match[1]),
      right: operandDescriptor(match[3])
    };
  }
  match = /^if \((not)?R\[I\.([ABC])\]\) then IP=I\.B end$/.exec(text);
  if (!match) {
    match = /^if (not)?R\[I\.([ABC])\] then IP=I\.B end$/.exec(text);
  }
  if (match) {
    return {
      kind: "test",
      truthy: !match[1],
      field: match[2].toLowerCase()
    };
  }
  return null;
}

function classifyCall(text) {
  if (!text.includes("R[t0]") || !text.includes("R[t0](")) return null;

  let args = "none";
  if (text.includes("TOP")) args = "top";
  else if (text.includes("I.B")) args = "b";
  else if (text.includes("R[(t0+1)]")) args = "one";

  let results = "none";
  if (text.includes("TOP=") && text.includes("v_h(")) results = "multi";
  else if (text.includes("local t1={R[t0](")) results = "fixed";
  else if (text.includes("R[t0]=R[t0](")) results = "one";

  return { kind: "call", args, results };
}

function normalizeTemporaryNames(text) {
  const names = new Map();
  return text.replace(/\bt\d+\b/g, (name) => {
    if (!names.has(name)) names.set(name, `t${names.size}`);
    return names.get(name);
  });
}

function normalizeHandlerText(text) {
  let normalized = text;
  while (/^local t\d+=[^;]+;function\(\.\.\.\)\{\};/.test(normalized)) {
    normalized = normalized.replace(
      /^local t\d+=[^;]+;function\(\.\.\.\)\{\};/,
      ""
    );
  }
  return normalizeTemporaryNames(normalized);
}

function classifyCompoundSegment(text) {
  const normalized = normalizeHandlerText(text);
  const arithmeticAndLength = normalized.match(
    /R\[I\.A\]=\(I\.B-I\.C\)|R\[I\.A\]=\(#R\[I\.B\]\)/g
  ) ?? [];
  if (
    arithmeticAndLength.length >= 2 &&
    normalized
      .replace(/local t\d+=;/g, "")
      .replace(/t\d+=I;/g, "")
      .replace(/R\[I\.A\]=\(I\.B-I\.C\);?/g, "")
      .replace(/R\[I\.A\]=\(#R\[I\.B\]\);?/g, "") === ""
  ) {
    return arithmeticAndLength.map((operation) => classifySegment(operation));
  }

  if (
    normalized.includes("t4=t3[t0[t1]]") &&
    normalized.includes("R[t5]=t4")
  ) {
    const reloads = normalized.match(/t0=I/g)?.length ?? 0;
    if (reloads > 0) {
      return Array.from({ length: reloads + 1 }, () => ({
        kind: "getglobal",
        keyField: "b",
        destinationField: "a"
      }));
    }
  }

  return null;
}

function classifySegment(text) {
  text = normalizeHandlerText(text);
  const branch = classifyBranch(text);
  if (branch) return branch;

  const exact = new Map([
    ["R[I.A]=I.B", { kind: "loadk" }],
    ["R[I.A]=R[I.B]", { kind: "move" }],
    ["R[I.A]=ENV[I.B]", { kind: "getglobal" }],
    ["ENV[I.B]=R[I.A]", { kind: "setglobal" }],
    ["R[I.A]=(I.B~=0)", { kind: "loadbool" }],
    ["R[I.A]=(#R[I.B])", { kind: "len" }],
    ["R[I.A]=(notR[I.B])", { kind: "not" }],
    ["R[I.A]=(-R[I.B])", { kind: "unm" }],
    ["IP=I.B", { kind: "jump" }],
    ["do return  end", { kind: "return", mode: "none" }],
    ["R[I.A]()", { kind: "call", args: "none", results: "none" }],
    ["R[I.A]={nil}", { kind: "newtable" }],
    ["R[I.A]=U[I.B]", { kind: "getupvalue" }],
    ["U[I.B]=R[I.A]", { kind: "setupvalue" }]
  ]);
  if (text === "return ") return { kind: "return", mode: "none" };
  if (exact.has(text)) return exact.get(text);

  let match = /^local t0=I\.([ABC]);local t1=I\.([ABC]);R\[t1\]=t0$/.exec(text);
  if (match) {
    return {
      kind: "loadk",
      sourceField: match[1].toLowerCase(),
      destinationField: match[2].toLowerCase()
    };
  }

  match = /^local t0=ENV;local t1=t0\[I\.([ABC])\];local t2=I\.([ABC]);R\[t2\]=t1$/.exec(text);
  if (match) {
    return {
      kind: "getglobal",
      keyField: match[1].toLowerCase(),
      destinationField: match[2].toLowerCase()
    };
  }

  match = /^local t0=I;local t1=R\[t0\[([ABC])\]\];local t2=t0\[([ABC])\];R\[t2\]=t1$/.exec(text);
  if (match) {
    return {
      kind: "move",
      sourceField: match[1].toLowerCase(),
      destinationField: match[2].toLowerCase()
    };
  }

  match = /^R\[I\.A\]=R\[I\.B\]\[(R\[I\.C\]|I\.C)\]$/.exec(text);
  if (match) return { kind: "gettable", key: match[1].startsWith("R[") ? "register" : "immediate" };

  match = /^R\[I\.A\]\[(R\[I\.B\]|I\.B)\]=(R\[I\.C\]|I\.C)$/.exec(text);
  if (match) {
    return {
      kind: "settable",
      key: match[1].startsWith("R[") ? "register" : "immediate",
      value: match[2].startsWith("R[") ? "register" : "immediate"
    };
  }

  match = /^R\[I\.A\]=\(?((?:R\[I\.B\]|I\.B))([+\-*/%^])((?:R\[I\.C\]|I\.C))\)?$/.exec(text);
  if (match) {
    return {
      kind: "binary",
      operator: match[2],
      left: match[1].startsWith("R[") ? "register" : "immediate",
      right: match[3].startsWith("R[") ? "register" : "immediate"
    };
  }

  if (/^local t0=I\.A;local t1=R\[I\.B\];R\[\(t0\+1\)\]=t1;R\[t0\]=t1\[/.test(text)) {
    return { kind: "self", key: text.includes("R[I.C]") ? "register" : "immediate" };
  }
  if (/^for t\d+=I\.A,I\.B do R\[t\d+\]=nil end$/.test(text)) return { kind: "loadnil" };
  if (text.includes("=WRAP(")) return { kind: "closure" };
  if (text.includes("..") && text.includes("R[I.A]=")) return { kind: "concat" };
  if (text.includes("50*(I.C-1)")) {
    return { kind: "setlist", mode: text.includes("TOP") ? "all" : "fixed" };
  }
  if (text.includes("v_x[") && text.includes("TOP=")) return { kind: "vararg", mode: "all" };
  if (text.includes("v_x[") && text.includes("for")) return { kind: "vararg", mode: "fixed" };
  if (text.includes("(t0+2)") && text.includes("IP=I.B") && text.includes("R[(t0+3)]")) {
    return { kind: text.includes("R[t0]+") ? "forloop" : "forprep" };
  }
  if (text.includes("local t2=(t0+2)") && text.includes("IP=I.B")) return { kind: "tforloop" };
  if (/^local t0=R\[I\.C\];if \(?nott0\)? then R\[I\.A\]=t0;IP=I\.B end$/.test(text)) {
    return { kind: "testset", truthy: false };
  }
  if (/^local t0=R\[I\.C\];if t0 then R\[I\.A\]=t0;IP=I\.B end$/.test(text)) {
    return { kind: "testset", truthy: true };
  }
  if (/^local t0=R\[I\.C\];R\[I\.A\]=t0;IP=I\.B$/.test(text)) {
    return { kind: "testset", truthy: null };
  }
  if (
    text.includes("return") &&
    (
      text.includes("R[I.A]") ||
      text.includes("R[t0]") ||
      /v_[A-Za-z0-9_]+\(R,t0/.test(text)
    )
  ) {
    if (text.includes("TOP")) return { kind: "return", mode: "top" };
    if (text.includes("I.B")) return { kind: "return", mode: "range" };
    if (text.includes("R[(t0+1)]")) return { kind: "return", mode: "two" };
    if (text.includes("R[I.A]()")) return { kind: "tailcall", args: "none" };
    return { kind: "return", mode: "one" };
  }
  if (text.includes("return R[t0](")) {
    return { kind: "tailcall", args: text.includes("TOP") ? "top" : text.includes("I.B") ? "b" : "none" };
  }

  const call = classifyCall(text);
  if (call) return call;

  if (text.includes("local t1=R[I.B]") && text.includes("R[(t0+1)]=t1")) {
    return { kind: "self", key: text.includes("R[I.C]") ? "register" : "immediate" };
  }
  if (/R\[I\.A\]=v_[A-Za-z0-9_]+\(.*\)/.test(text) || text.includes("R[I.A]={(nil)}")) {
    return { kind: "newtable" };
  }
  if (text.includes("for") && text.includes("=nil")) return { kind: "loadnil" };
  if (/^local t0=(?:I\.[ABC]|R\[I\.[ABC]\])$/.test(text)) return { kind: "nop" };
  if (/^local t0=I\.B;local t1=I\[[^\]]+\];R\[t1\]=t0$/.test(text)) {
    return { kind: "nop" };
  }
  if (
    /^local t0=ENV;local t1=t0\[I\.B\];local t2=I\[[^\]]+\];R\[t2\]=t1$/.test(text)
  ) {
    return { kind: "nop" };
  }
  if (
    /^local t0=I;local t1=R\[t0\[[^\]]+\]\];local t2=t0\[[^\]]+\];R\[t2\]=t1$/.test(text)
  ) {
    return { kind: "nop" };
  }
  if (text === "") return { kind: "nop" };
  return { kind: "unknown", handler: text };
}

export function classifyHandlers(analysis) {
  const fields = recoverFieldKeys(analysis.handlers);
  const constantAlias = recoverConstantAlias(analysis.handlers, fields);
  for (const handler of analysis.handlers.values()) {
    handler.microOps = handler.segments.flatMap((segment) => {
      let normalized = replaceFields(segment, fields);
      const constantFields = [];
      if (constantAlias) {
        normalized = normalized.replace(
          new RegExp(`${constantAlias.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}\\[I\\.([ABC])\\]`, "g"),
          (_, field) => {
            constantFields.push(field.toLowerCase());
            return `I.${field}`;
          }
        );
      }
      const operations = classifyCompoundSegment(normalized) ?? [
        classifySegment(normalized)
      ];
      return operations.map((operation) => ({
        ...operation,
        constantFields,
        normalized
      }));
    });
  }
  return { ...analysis, fields };
}

function expandChunk(chunk, handlers, path = "main") {
  const instructions = [];
  let index = 0;

  while (index < chunk.instructions.length) {
    const instruction = chunk.instructions[index];
    if (instruction.skipped) {
      instructions.push({ ...instruction, kind: "nop" });
      index += 1;
      continue;
    }

    const handler = handlers.get(instruction.op);
    const microOps = handler?.microOps ?? [
      { kind: "unknown", normalized: "", constantFields: [] }
    ];
    let consumed = 0;
    for (let offset = 0; offset < microOps.length && index + offset < chunk.instructions.length; offset += 1) {
      const encoded = chunk.instructions[index + offset];
      if (
        offset > 0 &&
        !canApplyMicroOperation(encoded, microOps[offset], chunk)
      ) {
        break;
      }
      const expanded = recoverUnknownInstruction({
        ...encoded,
        ...microOps[offset],
        superOpcode: instruction.op,
        superOffset: offset
      });
      instructions.push(expanded);
      consumed += 1;
      for (const field of expanded.constantFields ?? []) {
        const upper = field.toUpperCase();
        expanded[`constant${upper}`] = true;
        expanded[`value${upper}`] = chunk.constants[expanded[field]];
      }
    }
    index += Math.max(1, consumed);
  }

  return {
    ...chunk,
    path,
    instructions,
    protos: chunk.protos.map((child, childIndex) =>
      expandChunk(child, handlers, `${path}_${childIndex}`)
    )
  };
}

export function expandSuperinstructions(chunk, analysis) {
  return expandChunk(chunk, analysis.handlers);
}

export function scoreChunkSemantics(chunk, analysis) {
  return scoreExpandedChunk(expandSuperinstructions(chunk, analysis));
}

function scoreExpandedChunk(chunk) {
  let score = 0;
  const registerLimit = Math.max(chunk.stackSize, chunk.parameterCount, 1);

  for (const instruction of chunk.instructions) {
    if (instruction.kind === "unknown") score -= 1_000_000;

    for (const field of instruction.constantFields ?? []) {
      const value = instruction[field];
      score += Number.isInteger(value) &&
        value >= 0 &&
        value < chunk.constants.length
        ? 2_000
        : -1_000_000_000;
    }

    const expectedType = expectedInstructionType(instruction.kind);
    if (expectedType !== null) {
      score += instruction.type === expectedType ? 1_000 : -20_000;
    }

    for (const field of registerFields(instruction)) {
      if (instruction.constantFields?.includes(field)) continue;
      const value = instruction[field];
      if (Number.isInteger(value) && value >= 0 && value < registerLimit) {
        score += 100;
      } else {
        score -= 50_000;
      }
    }
  }

  for (const child of chunk.protos) score += scoreExpandedChunk(child);
  return score;
}

function expectedInstructionType(kind) {
  if (["loadk", "getglobal", "setglobal"].includes(kind)) return 1;
  if (["jump", "forprep", "forloop"].includes(kind)) return 2;
  if (["branch", "test", "testset", "tforloop"].includes(kind)) return 3;
  if (kind === "closure") return 5;
  if (
    [
      "move", "loadbool", "loadnil", "getupvalue", "setupvalue",
      "gettable", "settable", "self", "newtable", "binary", "unm",
      "not", "len", "concat", "call", "tailcall", "return", "setlist",
      "vararg"
    ].includes(kind)
  ) {
    return 0;
  }
  return null;
}

function registerFields(instruction) {
  switch (instruction.kind) {
    case "loadk":
    case "getglobal":
      return [instruction.destinationField ?? "a"];
    case "setglobal":
    case "setupvalue":
      return ["a"];
    case "move":
      return [
        instruction.destinationField ?? "a",
        instruction.sourceField ?? "b"
      ];
    case "gettable":
      return ["a", "b", "c"];
    case "settable":
      return ["a", "b", "c"];
    case "self":
      return ["a", "b", "c"];
    case "binary":
      return ["a", "b", "c"];
    case "unm":
    case "not":
    case "len":
      return ["a", "b"];
    case "concat":
      return ["a", "b", "c"];
    case "loadbool":
    case "loadnil":
    case "getupvalue":
    case "newtable":
    case "call":
    case "tailcall":
    case "return":
    case "setlist":
    case "vararg":
    case "closure":
      return ["a"];
    case "branch":
      return [instruction.left?.field, instruction.right?.field].filter(Boolean);
    case "test":
      return [instruction.field ?? "a"];
    case "testset":
      return ["a", "c"];
    case "forprep":
    case "forloop":
    case "tforloop":
      return ["a"];
    default:
      return [];
  }
}

function canApplyMicroOperation(instruction, operation, chunk) {
  const controlKinds = new Set(["jump", "forprep", "forloop"]);
  const conditionalKinds = new Set(["branch", "test", "testset", "tforloop"]);
  if (instruction.type === 2 && !controlKinds.has(operation.kind)) return false;
  if (instruction.type === 3 && !conditionalKinds.has(operation.kind)) return false;
  if (instruction.type === 5 && operation.kind !== "closure") return false;

  for (const field of operation.constantFields ?? []) {
    const value = instruction[field];
    if (
      !Number.isInteger(value) ||
      value < 0 ||
      value >= chunk.constants.length
    ) {
      return false;
    }
  }

  const registerLimit = Math.max(chunk.stackSize, chunk.parameterCount, 1);
  const validRegister = (field) => {
    const value = instruction[field];
    return Number.isInteger(value) && value >= 0 && value < registerLimit;
  };
  const isConstant = (field) =>
    operation.constantFields?.includes(field) ?? false;

  switch (operation.kind) {
    case "loadk":
    case "getglobal":
      return validRegister(operation.destinationField ?? "a");
    case "move":
      return (
        validRegister(operation.destinationField ?? "a") &&
        validRegister(operation.sourceField ?? "b")
      );
    case "gettable":
      return (
        validRegister("a") &&
        validRegister("b") &&
        (isConstant("c") || validRegister("c"))
      );
    case "settable":
      return (
        validRegister("a") &&
        (isConstant("b") || validRegister("b")) &&
        (isConstant("c") || validRegister("c"))
      );
    case "self":
      return (
        validRegister("a") &&
        instruction.a + 1 < registerLimit &&
        validRegister("b") &&
        (isConstant("c") || validRegister("c"))
      );
    default:
      return true;
  }
}
