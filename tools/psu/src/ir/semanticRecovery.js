function containsStackWrite(text) {
  return /R\[[^\]]+\]=/.test(text);
}

function containsEnvironmentRead(text) {
  return /\bENV\b/.test(text);
}

function containsExternalEffect(text) {
  return (
    /\b(?:R|ENV|U)\[/.test(text) ||
    /\b(?:IP|TOP)=/.test(text) ||
    /\breturn\b/.test(text) ||
    /\bWRAP\(/.test(text)
  );
}

function recoverDirectOperation(text) {
  if (/R\[I\.A\]\[I\.B\]=R\[I\.C\]/.test(text)) {
    return { kind: "settable", key: "register", value: "register" };
  }
  if (/R\[I\.A\]=R\[I\.B\]\[I\.C\]/.test(text)) {
    return { kind: "gettable", key: "register" };
  }
  if (/ENV\[I\.B\]=R\[I\.A\]/.test(text)) return { kind: "setglobal" };
  if (/R\[I\.A\]=U\[I\.B\]/.test(text)) return { kind: "getupvalue" };
  if (/U\[I\.B\]=R\[I\.A\]/.test(text)) return { kind: "setupvalue" };
  if (/R\[I\.A\]=\(I\.B~=0\)/.test(text)) return { kind: "loadbool" };
  if (/R\[I\.A\]=\(#R\[I\.B\]\)/.test(text)) return { kind: "len" };
  if (/R\[I\.A\]=\(notR\[I\.B\]\)/.test(text)) return { kind: "not" };
  if (/R\[I\.A\]=\(-R\[I\.B\]\)/.test(text)) return { kind: "unm" };
  if (/R\[I\.A\]=\{nil\}/.test(text)) return { kind: "newtable" };
  return null;
}

function recoverBinary(text) {
  const match = /R\[I\.A\]=\(?(R\[I\.B\]|I\.B)([+\-*/%^])(R\[I\.C\]|I\.C)\)?/.exec(text);
  if (!match) return null;
  return {
    kind: "binary",
    operator: match[2],
    left: match[1].startsWith("R[") ? "register" : "immediate",
    right: match[3].startsWith("R[") ? "register" : "immediate"
  };
}

function recoverVararg(text) {
  if (
    /\bfor t\d+=1,t\d+,1 do R\[\(\(t\d+\+t\d+\)-1\)\]=/.test(text) &&
    /\[\(t\d+-1\)\]/.test(text)
  ) {
    return { kind: "vararg", mode: "fixed" };
  }
  return null;
}

function recoverEncodedOperation(instruction, text) {
  if (instruction.type === 1 && instruction.constantB) {
    if (containsEnvironmentRead(text)) {
      return {
        kind: "getglobal",
        keyField: "b",
        destinationField: "a"
      };
    }
    if (containsStackWrite(text)) {
      return {
        kind: "loadk",
        sourceField: "b",
        destinationField: "a"
      };
    }
  }

  if (
    instruction.type === 0 &&
    !instruction.constantA &&
    !instruction.constantB &&
    !instruction.constantC &&
    containsStackWrite(text) &&
    !containsEnvironmentRead(text) &&
    !/\b(?:IP|TOP)=|\breturn\b|\bWRAP\(/.test(text)
  ) {
    return {
      kind: "move",
      sourceField: "b",
      destinationField: "a"
    };
  }

  return null;
}

export function recoverUnknownInstruction(instruction) {
  if (instruction.kind !== "unknown") return instruction;

  const text = instruction.normalized ?? instruction.handler ?? "";
  const operation =
    recoverDirectOperation(text) ??
    recoverBinary(text) ??
    recoverVararg(text) ??
    recoverEncodedOperation(instruction, text) ??
    (instruction.type === 2 ? { kind: "jump" } : null) ??
    (!containsExternalEffect(text) ? { kind: "nop" } : null);

  return operation ? { ...instruction, ...operation } : instruction;
}
