import luaparse from "luaparse";

const DECOMPILER_TEMP = /^(?:L|A)\d+_\d+$/;
const SIMPLE_TEMP = /^v(\d+)$/;

function visit(node, callback, parent = null) {
  if (!node || typeof node !== "object") return;
  callback(node, parent);
  for (const [key, value] of Object.entries(node)) {
    if (key === "loc" || key === "range") continue;
    if (Array.isArray(value)) {
      for (const child of value) visit(child, callback, node);
    } else {
      visit(value, callback, node);
    }
  }
}

function isPropertyIdentifier(node, parent) {
  return (
    parent?.type === "MemberExpression" && parent.identifier === node
  ) || (
    parent?.type === "TableKeyString" && parent.key === node
  );
}

function parse(source) {
  return luaparse.parse(source, {
    luaVersion: "5.2",
    ranges: true,
    encodingMode: "x-user-defined"
  });
}

function renameDecompilerTemporaries(source) {
  let ast;
  try {
    ast = parse(source);
  } catch {
    return source;
  }

  const reserved = new Set();
  const occurrences = [];
  const firstPosition = new Map();

  visit(ast, (node, parent) => {
    if (node.type !== "Identifier" || isPropertyIdentifier(node, parent)) return;

    if (DECOMPILER_TEMP.test(node.name)) {
      occurrences.push(node);
      if (!firstPosition.has(node.name)) firstPosition.set(node.name, node.range[0]);
      return;
    }

    const match = SIMPLE_TEMP.exec(node.name);
    if (match) reserved.add(Number(match[1]));
  });

  const orderedNames = [...firstPosition]
    .sort((left, right) => left[1] - right[1])
    .map(([name]) => name);

  const names = new Map();
  let index = 1;
  for (const name of orderedNames) {
    while (reserved.has(index)) index += 1;
    names.set(name, `v${index}`);
    reserved.add(index);
    index += 1;
  }

  const replacements = occurrences
    .map((node) => ({
      start: node.range[0],
      end: node.range[1],
      value: names.get(node.name)
    }))
    .sort((left, right) => right.start - left.start);

  let output = source;
  for (const replacement of replacements) {
    output = output.slice(0, replacement.start) +
      replacement.value +
      output.slice(replacement.end);
  }
  return output;
}

function foldAccessChains(source) {
  const lines = source.replace(/\r\n/g, "\n").split("\n");
  const output = [];
  const assignment = /^(\s*)(v\d+)\s*=\s*(.+?)\s*$/;

  for (const line of lines) {
    const current = assignment.exec(line);
    const previous = output.length > 0 ? assignment.exec(output.at(-1)) : null;

    if (current && previous && current[1] === previous[1] && current[2] === previous[2]) {
      const variable = current[2];
      const right = current[3];
      if (right.startsWith(`${variable}.`) || right.startsWith(`${variable}[`)) {
        const suffix = right.slice(variable.length);
        output[output.length - 1] = `${previous[1]}${variable} = ${previous[3]}${suffix}`;
        continue;
      }
    }

    output.push(line);
  }

  return output.join("\n");
}


function escapeRegExp(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

function isDeadAfter(lines, startIndex, variable) {
  const name = escapeRegExp(variable);
  const use = new RegExp(`\\b${name}\\b`);
  const assignment = new RegExp(`^\\s*${name}\\s*=`);

  for (let index = startIndex; index < lines.length; index += 1) {
    const line = lines[index];
    if (assignment.test(line)) return true;
    if (use.test(line)) return false;
  }
  return true;
}

function foldMethodCalls(source) {
  const lines = source.split("\n");
  const assignment = /^(\s*)(v\d+)\s*=\s*(.+?)\s*$/;
  const output = [];

  for (let index = 0; index < lines.length; index += 1) {
    const base = assignment.exec(lines[index]);
    const receiver = assignment.exec(lines[index + 1] ?? "");
    const method = assignment.exec(lines[index + 2] ?? "");

    if (!base || !receiver || !method || base[1] !== receiver[1] || base[1] !== method[1]) {
      output.push(lines[index]);
      continue;
    }

    const target = base[2];
    const self = receiver[2];
    if (receiver[3] !== target || method[2] !== target) {
      output.push(lines[index]);
      continue;
    }

    const methodMatch = new RegExp(`^${escapeRegExp(target)}\\.([A-Za-z_][A-Za-z0-9_]*)$`).exec(method[3]);
    if (!methodMatch) {
      output.push(lines[index]);
      continue;
    }

    const args = [];
    let cursor = index + 3;
    while (cursor < lines.length) {
      const candidate = assignment.exec(lines[cursor]);
      if (!candidate || candidate[1] !== base[1] || candidate[2] === target || candidate[2] === self) break;
      args.push({ variable: candidate[2], expression: candidate[3] });
      cursor += 1;
    }

    const call = assignment.exec(lines[cursor] ?? "");
    if (!call || call[1] !== base[1] || call[2] !== target) {
      output.push(lines[index]);
      continue;
    }

    const expectedArgs = [self, ...args.map((arg) => arg.variable)].join(", ");
    const callPattern = new RegExp(
      `^${escapeRegExp(target)}\\(${escapeRegExp(expectedArgs)}\\)(.*)$`
    );
    const callMatch = callPattern.exec(call[3]);
    if (!callMatch) {
      output.push(lines[index]);
      continue;
    }

    const dead = [self, ...args.map((arg) => arg.variable)]
      .every((variable) => isDeadAfter(lines, cursor + 1, variable));
    if (!dead) {
      output.push(lines[index]);
      continue;
    }

    const values = args.map((arg) => arg.expression).join(", ");
    output.push(
      `${base[1]}${target} = ${base[3]}:${methodMatch[1]}(${values})${callMatch[1]}`
    );
    index = cursor;
  }

  return output.join("\n");
}


function foldDirectCalls(source) {
  const lines = source.split("\n");
  const assignment = /^(\s*)(v\d+)\s*=\s*(.+?)\s*$/;
  const output = [];

  for (let index = 0; index < lines.length; index += 1) {
    const current = assignment.exec(lines[index]);
    const next = assignment.exec(lines[index + 1] ?? "");

    if (current && next && current[1] === next[1] && current[2] === next[2]) {
      const variable = current[2];
      const call = new RegExp(`^${escapeRegExp(variable)}\\((.*)\\)(.*)$`).exec(next[3]);
      if (call && !new RegExp(`\\b${escapeRegExp(variable)}\\b`).test(current[3])) {
        output.push(`${current[1]}${variable} = ${current[3]}(${call[1]})${call[2]}`);
        index += 1;
        continue;
      }
    }

    output.push(lines[index]);
  }

  return output.join("\n");
}

function inlineDeadSimpleAssignments(source) {
  const lines = source.split("\n");
  const assignment = /^(\s*)(v\d+)\s*=\s*(.+?)\s*$/;
  const simpleValue = /^(?:[A-Za-z_][A-Za-z0-9_]*|true|false|nil|-?\d+(?:\.\d+)?|"(?:[^"\\]|\\.)*"|'(?:[^'\\]|\\.)*')$/;
  const output = [];

  for (let index = 0; index < lines.length; index += 1) {
    const current = assignment.exec(lines[index]);
    const next = lines[index + 1];
    if (!current || !next || !simpleValue.test(current[3])) {
      output.push(lines[index]);
      continue;
    }

    const variable = current[2];
    const name = new RegExp(`\\b${escapeRegExp(variable)}\\b`, "g");
    const matches = [...next.matchAll(name)];
    const nextAssignsVariable = new RegExp(`^\\s*${escapeRegExp(variable)}\\s*=`).test(next);

    if (
      matches.length === 1 &&
      !nextAssignsVariable &&
      isDeadAfter(lines, index + 2, variable)
    ) {
      output.push(next.replace(name, current[3]));
      index += 1;
      continue;
    }

    output.push(lines[index]);
  }

  return output.join("\n");
}


function pruneUnusedTempDeclarations(source) {
  const lines = source.split("\n");
  const counts = new Map();

  for (const match of source.matchAll(/\bv\d+\b/g)) {
    counts.set(match[0], (counts.get(match[0]) ?? 0) + 1);
  }

  return lines
    .map((line) => {
      const match = /^(\s*)local\s+(v\d+(?:\s*,\s*v\d+)*)\s*$/.exec(line);
      if (!match) return line;

      const variables = match[2]
        .split(",")
        .map((name) => name.trim())
        .filter((name) => (counts.get(name) ?? 0) > 1);

      return variables.length > 0 ? `${match[1]}local ${variables.join(", ")}` : "";
    })
    .filter((line, index, all) => line !== "" || all[index - 1] !== "")
    .join("\n");
}

function normalizeWhitespace(source) {
  return source
    .replace(/[ \t]+$/gm, "")
    .replace(/\n{3,}/g, "\n\n")
    .trim();
}

export function cleanDecompiledSource(source, options = {}) {
  const {
    renameVariables = true,
    foldChains = true
  } = options;

  let output = source;
  if (renameVariables) output = renameDecompilerTemporaries(output);
  if (foldChains) {
    output = foldAccessChains(output);
    output = foldMethodCalls(output);
    output = foldDirectCalls(output);
    output = inlineDeadSimpleAssignments(output);
    output = foldDirectCalls(output);
    output = foldAccessChains(output);
    output = pruneUnusedTempDeclarations(output);
  }
  return `${normalizeWhitespace(output)}\n`;
}
