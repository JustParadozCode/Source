import luaparse from "luaparse";
import {
  evaluateStatic,
  extendStaticEnvironment,
  identifierName,
  seedTopEnvironment,
  walk
} from "./staticEval.js";

function operatorCounts(node) {
  const counts = new Map();
  walk(node, (child) => {
    if (typeof child.operator === "string") {
      counts.set(child.operator, (counts.get(child.operator) ?? 0) + 1);
    }
  });
  return counts;
}

function conditionValue(condition, environment) {
  if (!condition) return null;
  if (condition.type === "BinaryExpression") {
    for (const side of [condition.right, condition.left]) {
      try {
        const value = evaluateStatic(side, environment);
        if (typeof value === "number") return value;
      } catch {
      }
    }
  }
  if (condition.type === "CallExpression") {
    for (const argument of [...condition.arguments].reverse()) {
      try {
        const value = evaluateStatic(argument, environment);
        if (typeof value === "number") return value;
      } catch {
      }
    }
  }
  return null;
}

function conditionModulo(condition, environment) {
  let found = null;
  walk(condition, (candidate) => {
    if (found !== null || candidate?.type !== "BinaryExpression" || candidate.operator !== "%") {
      return;
    }
    try {
      const value = evaluateStatic(candidate.right, environment);
      if (Number.isInteger(value) && value > 0) found = value;
    } catch {
    }
  });
  return found;
}

function findConstantTags(wrapper, topEnvironment) {
  let found = null;

  function visitFunction(node, parentEnvironment) {
    if (found) return;
    const environment = extendStaticEnvironment(node.body, parentEnvironment);

    function scan(candidate) {
      if (!candidate || typeof candidate !== "object" || found) return;
      if (candidate.type === "FunctionDeclaration" && candidate !== node) {
        visitFunction(candidate, environment);
        return;
      }

      if (
        candidate.type === "IfStatement" &&
        candidate.clauses.length >= 4 &&
        candidate.clauses.length <= 8
      ) {
        const tagged = candidate.clauses
          .filter((clause) => clause.condition)
          .map((clause) => ({
            clause,
            tag: conditionValue(clause.condition, environment),
            operators: operatorCounts(clause)
          }));
        if (tagged.length >= 3 && tagged.every((entry) => entry.tag !== null)) {
          const number = tagged.find((entry) => entry.operators.has("^"));
          const string = tagged.find((entry) => entry.operators.has("..")) ??
            tagged.find((entry) => entry !== number && entry.operators.has("#"));
          const remaining = tagged.filter((entry) => entry !== number && entry !== string);
          let boolean = remaining.find((entry) => entry.operators.has("~="));
          let fastString = remaining.find((entry) => entry !== boolean);
          if (!boolean && remaining.length > 0) {
            fastString = remaining.length > 1
              ? remaining.find((entry) =>
                entry.operators.has("+") || entry.operators.has("-")
              ) ?? remaining.at(-1)
              : null;
            boolean = remaining.find((entry) => entry !== fastString) ?? remaining[0];
          }
          if (boolean && number && string) {
            found = {
              boolean: boolean.tag,
              number: number.tag,
              string: string.tag,
              fastString: fastString?.tag ?? null,
              modulo: tagged
                .map((entry) => conditionModulo(entry.clause.condition, environment))
                .find((value) => value !== null) ?? null
            };
            return;
          }
        }
      }

      for (const [key, value] of Object.entries(candidate)) {
        if (key === "loc" || key === "range") continue;
        if (Array.isArray(value)) {
          for (const child of value) scan(child);
        } else {
          scan(value);
        }
      }
    }

    for (const statement of node.body) scan(statement);
  }

  visitFunction(wrapper, topEnvironment);
  if (!found) throw new Error("Unsupported PSU constant decoder");
  return found;
}

function findRuntime(wrapper, topEnvironment) {
  let runtime = null;

  function visitFunction(node, parentEnvironment) {
    if (runtime) return;
    const environment = extendStaticEnvironment(node.body, parentEnvironment);
    const returned = node.body.find((statement) => statement.type === "ReturnStatement");
    const inner = returned?.arguments?.find(
      (argument) => argument.type === "FunctionDeclaration"
    );

    if (inner) {
      const innerEnvironment = extendStaticEnvironment(inner.body, environment);
      const loop = inner.body.find((statement) =>
        statement.type === "WhileStatement" ||
        statement.type === "RepeatStatement"
      );
      const body = loop?.body ?? [];
      const loadInstruction = body[0];
      const loadOpcode = body[1];
      const advance = body[2];
      const dispatch = body[3];

      const instructionName = identifierName(loadInstruction?.variables?.[0]);
      const instructionPointName = identifierName(loadInstruction?.init?.[0]);
      const opcodeName = identifierName(loadOpcode?.variables?.[0]);
      const opcodeIndex = loadOpcode?.init?.[0];
      const nextIndex = advance?.init?.[0];

      if (
        loadInstruction?.type === "LocalStatement" &&
        loadOpcode?.type === "LocalStatement" &&
        advance?.type === "AssignmentStatement" &&
        dispatch?.type === "IfStatement" &&
        opcodeIndex?.type === "IndexExpression" &&
        nextIndex?.type === "IndexExpression" &&
        identifierName(opcodeIndex.base) === instructionName &&
        identifierName(nextIndex.base) === instructionName
      ) {
        let enumKey;
        let nextKey;
        try {
          enumKey = evaluateStatic(opcodeIndex.index, innerEnvironment);
          nextKey = evaluateStatic(nextIndex.index, innerEnvironment);
        } catch {
          enumKey = null;
          nextKey = null;
        }

        if (enumKey !== null && nextKey !== null) {
          runtime = {
            outer: node,
            inner,
            loop,
            dispatch,
            environment: innerEnvironment,
            instructionName,
            instructionPointName,
            opcodeName,
            enumKey,
            nextKey,
            upvaluesName: identifierName(node.parameters[1]),
            environmentName: identifierName(node.parameters[2]),
            factoryName: identifierName(node.identifier)
          };
          return;
        }
      }
    }

    for (const statement of node.body) {
      if (statement.type === "FunctionDeclaration") {
        visitFunction(statement, environment);
      } else if (statement.type === "LocalStatement") {
        for (const init of statement.init) {
          if (init?.type === "FunctionDeclaration") visitFunction(init, environment);
        }
      }
    }
  }

  visitFunction(wrapper, topEnvironment);
  if (!runtime) throw new Error("Unsupported PSU virtual machine");

  const excluded = new Set([
    runtime.instructionName,
    runtime.instructionPointName,
    runtime.opcodeName,
    runtime.upvaluesName,
    runtime.environmentName
  ]);
  const counts = new Map();
  walk(runtime.dispatch, (node) => {
    if (node.type !== "IndexExpression") return;
    const name = identifierName(node.base);
    if (name && !excluded.has(name)) counts.set(name, (counts.get(name) ?? 0) + 1);
  });
  const loopIndex = runtime.inner.body.indexOf(runtime.loop);
  const argumentLoop = runtime.inner.body
    .slice(0, loopIndex)
    .reverse()
    .find((statement) => statement.type === "ForNumericStatement");
  const argumentBranch = argumentLoop?.body?.find(
    (statement) => statement.type === "IfStatement"
  );
  const stackAssignment = argumentBranch?.clauses?.at(-1)?.body?.find(
    (statement) =>
      statement.type === "AssignmentStatement" &&
      statement.variables[0]?.type === "IndexExpression"
  );
  runtime.stackName = identifierName(stackAssignment?.variables?.[0]?.base) ??
    [...counts].sort((left, right) => right[1] - left[1])[0]?.[0] ??
    null;

  for (const statement of runtime.inner.body) {
    if (statement.type !== "LocalStatement") continue;
    for (let index = 0; index < statement.variables.length; index += 1) {
      const init = statement.init[index];
      if (
        init?.type === "UnaryExpression" &&
        init.operator === "-" &&
        init.argument?.type === "NumericLiteral" &&
        init.argument.value === 1
      ) {
        runtime.topName = identifierName(statement.variables[index]);
      }
    }
  }

  return runtime;
}

function selectDispatchBody(statement, runtime, opcode) {
  const environment = new Map(runtime.environment);
  environment.set(runtime.opcodeName, opcode);

  for (const clause of statement.clauses) {
    if (!clause.condition) return clause.body;
    try {
      if (evaluateStatic(clause.condition, environment)) return clause.body;
    } catch {
      return null;
    }
  }
  return [];
}

function resolveHandlerBody(runtime, opcode) {
  const environment = new Map(runtime.environment);
  environment.set(runtime.opcodeName, opcode);
  for (const name of [
    runtime.instructionName,
    runtime.instructionPointName,
    runtime.stackName,
    runtime.upvaluesName,
    runtime.environmentName,
    runtime.topName
  ]) {
    if (name && name !== runtime.opcodeName) environment.delete(name);
  }

  function flatten(statements, scope, depth = 0) {
    if (depth > 200) throw new Error("PSU static control flow is too deep");
    const output = [];

    for (const statement of statements) {
      if (statement.type === "FunctionDeclaration") {
        if (statement.identifier?.name) {
          scope.set(
            statement.identifier.name,
            evaluateStatic(statement, scope)
          );
        }
        continue;
      }

      if (statement.type === "LocalStatement") {
        if (statement.init.length === 0) {
          output.push(statement);
          continue;
        }
        const pending = [];
        let isStatic = true;
        for (let index = 0; index < statement.variables.length; index += 1) {
          const name = identifierName(statement.variables[index]);
          const init = statement.init[index];
          if (!name || !init) {
            isStatic = false;
            break;
          }
          try {
            pending.push([name, evaluateStatic(init, scope)]);
          } catch {
            isStatic = false;
            break;
          }
        }
        if (isStatic) {
          for (const [name, value] of pending) scope.set(name, value);
        } else {
          for (const variable of statement.variables) {
            const name = identifierName(variable);
            if (name) scope.delete(name);
          }
          output.push(statement);
        }
        continue;
      }

      if (statement.type === "AssignmentStatement") {
        let isStatic = statement.variables.every((variable) => variable.type === "Identifier");
        const values = [];
        if (isStatic) {
          for (const init of statement.init) {
            try {
              values.push(evaluateStatic(init, scope));
            } catch {
              isStatic = false;
              break;
            }
          }
        }
        if (isStatic) {
          for (let index = 0; index < statement.variables.length; index += 1) {
            scope.set(statement.variables[index].name, values[index] ?? null);
          }
        } else {
          for (const variable of statement.variables) {
            const name = identifierName(variable);
            if (name) scope.delete(name);
          }
          output.push(statement);
        }
        continue;
      }

      if (statement.type === "IfStatement") {
        let selected = null;
        let staticCondition = true;
        for (const clause of statement.clauses) {
          if (!clause.condition) {
            selected = clause.body;
            break;
          }
          try {
            if (evaluateStatic(clause.condition, scope)) {
              selected = clause.body;
              break;
            }
          } catch {
            staticCondition = false;
            break;
          }
        }
        if (staticCondition) {
          if (selected) output.push(...flatten(selected, scope, depth + 1));
        } else {
          output.push(statement);
        }
        continue;
      }

      if (statement.type === "ForNumericStatement") {
        try {
          const start = evaluateStatic(statement.start, scope);
          const end = evaluateStatic(statement.end, scope);
          const step = statement.step ? evaluateStatic(statement.step, scope) : 1;
          if (
            !Number.isFinite(start) ||
            !Number.isFinite(end) ||
            !Number.isFinite(step) ||
            step === 0 ||
            Math.abs((end - start) / step) > 10_000
          ) {
            throw new Error("Invalid static loop");
          }
          const loopScope = new Map(scope);
          for (
            let value = start;
            step > 0 ? value <= end : value >= end;
            value += step
          ) {
            loopScope.set(statement.variable.name, value);
            output.push(...flatten(statement.body, loopScope, depth + 1));
          }
          for (const [name, value] of loopScope) {
            if (scope.has(name)) scope.set(name, value);
          }
        } catch {
          output.push(statement);
        }
        continue;
      }

      if (statement.type === "DoStatement") {
        output.push(...flatten(statement.body, new Map(scope), depth + 1));
        continue;
      }

      output.push(statement);
    }

    return output;
  }

  return flatten([runtime.dispatch], environment);
}

class CanonicalScope {
  constructor(parent = null) {
    this.parent = parent;
    this.bindings = new Map();
    this.counter = parent?.counter ?? { value: 0 };
  }

  child() {
    return new CanonicalScope(this);
  }

  bind(name, value = null) {
    const canonical = value ?? `t${this.counter.value++}`;
    this.bindings.set(name, canonical);
    return canonical;
  }

  get(name) {
    if (this.bindings.has(name)) return this.bindings.get(name);
    return this.parent?.get(name) ?? `v_${name}`;
  }
}

function luaLiteral(value) {
  if (value === null) return "nil";
  if (typeof value === "string") return JSON.stringify(value);
  if (typeof value === "boolean") return value ? "true" : "false";
  return String(value);
}

function printExpression(node, scope, runtime) {
  if (!node) return "";
  switch (node.type) {
    case "Identifier": return scope.get(node.name);
    case "NumericLiteral":
    case "StringLiteral":
    case "BooleanLiteral": return luaLiteral(node.value);
    case "NilLiteral": return "nil";
    case "VarargLiteral": return "...";
    case "UnaryExpression":
      return `(${node.operator}${printExpression(node.argument, scope, runtime)})`;
    case "BinaryExpression":
    case "LogicalExpression":
      return `(${printExpression(node.left, scope, runtime)}${node.operator}${printExpression(node.right, scope, runtime)})`;
    case "IndexExpression": {
      const base = printExpression(node.base, scope, runtime);
      let index;
      if (base === "I") {
        try {
          const value = evaluateStatic(node.index, runtime.environment);
          if (["string", "number", "boolean"].includes(typeof value) || value === null) {
            index = luaLiteral(value);
          } else {
            index = printExpression(node.index, scope, runtime);
          }
        } catch {
          index = printExpression(node.index, scope, runtime);
        }
      } else {
        index = printExpression(node.index, scope, runtime);
      }
      return `${base}[${index}]`;
    }
    case "MemberExpression":
      return `${printExpression(node.base, scope, runtime)}.${node.identifier.name}`;
    case "CallExpression":
      return `${printExpression(node.base, scope, runtime)}(${node.arguments.map((argument) =>
        printExpression(argument, scope, runtime)
      ).join(",")})`;
    case "TableCallExpression":
      return `${printExpression(node.base, scope, runtime)}${printExpression(node.arguments, scope, runtime)}`;
    case "StringCallExpression":
      return `${printExpression(node.base, scope, runtime)}${printExpression(node.argument, scope, runtime)}`;
    case "TableConstructorExpression":
      return `{${node.fields.map((field) => {
        if (field.type === "TableValue") return printExpression(field.value, scope, runtime);
        if (field.type === "TableKeyString") {
          return `${field.key.name}=${printExpression(field.value, scope, runtime)}`;
        }
        return `[${printExpression(field.key, scope, runtime)}]=${printExpression(field.value, scope, runtime)}`;
      }).join(",")}}`;
    case "FunctionDeclaration":
      return "function(...)";
    default:
      return `<${node.type}>`;
  }
}

function printBlock(statements, parentScope, runtime) {
  const scope = parentScope.child();
  return statements.map((statement) => printStatement(statement, scope, runtime)).join(";");
}

function printStatement(statement, scope, runtime) {
  switch (statement.type) {
    case "LocalStatement": {
      const values = statement.init.map((init) => printExpression(init, scope, runtime));
      const names = statement.variables.map((variable) =>
        scope.bind(identifierName(variable) ?? "_")
      );
      return values.length > 0
        ? `local ${names.join(",")}=${values.join(",")}`
        : `local ${names.join(",")}`;
    }
    case "AssignmentStatement":
      return `${statement.variables.map((node) => printExpression(node, scope, runtime)).join(",")}=${statement.init.map((node) =>
        printExpression(node, scope, runtime)
      ).join(",")}`;
    case "CallStatement":
      return printExpression(statement.expression, scope, runtime);
    case "ReturnStatement":
      return `return ${statement.arguments.map((node) => printExpression(node, scope, runtime)).join(",")}`;
    case "BreakStatement":
      return "break";
    case "DoStatement":
      return `do ${printBlock(statement.body, scope, runtime)} end`;
    case "WhileStatement":
      return `while ${printExpression(statement.condition, scope, runtime)} do ${printBlock(statement.body, scope, runtime)} end`;
    case "RepeatStatement":
      return `repeat ${printBlock(statement.body, scope, runtime)} until ${printExpression(statement.condition, scope, runtime)}`;
    case "IfStatement":
      return statement.clauses.map((clause, index) => {
        const prefix = index === 0 ? "if" : clause.condition ? "elseif" : "else";
        const condition = clause.condition
          ? ` ${printExpression(clause.condition, scope, runtime)} then`
          : "";
        return `${prefix}${condition} ${printBlock(clause.body, scope, runtime)}`;
      }).join(" ") + " end";
    case "ForNumericStatement": {
      const child = scope.child();
      const name = child.bind(statement.variable.name);
      const step = statement.step ? `,${printExpression(statement.step, scope, runtime)}` : "";
      return `for ${name}=${printExpression(statement.start, scope, runtime)},${printExpression(statement.end, scope, runtime)}${step} do ${printBlock(statement.body, child, runtime)} end`;
    }
    case "ForGenericStatement": {
      const child = scope.child();
      const names = statement.variables.map((variable) => child.bind(variable.name));
      return `for ${names.join(",")} in ${statement.iterators.map((node) =>
        printExpression(node, scope, runtime)
      ).join(",")} do ${printBlock(statement.body, child, runtime)} end`;
    }
    default:
      return `<${statement.type}>`;
  }
}

function isInstructionReload(statement, runtime) {
  if (
    statement?.type !== "AssignmentStatement" ||
    statement.variables.length !== 1 ||
    identifierName(statement.variables[0]) !== runtime.instructionName
  ) {
    return false;
  }
  const value = statement.init[0];
  if (
    value?.type !== "IndexExpression" ||
    identifierName(value.base) !== runtime.instructionName
  ) {
    return false;
  }
  try {
    return evaluateStatic(value.index, runtime.environment) === runtime.nextKey;
  } catch {
    return false;
  }
}

function splitSegments(body, runtime) {
  const segments = [[]];
  for (const statement of body) {
    if (isInstructionReload(statement, runtime)) {
      if (segments.at(-1).length > 0) segments.push([]);
    } else {
      segments.at(-1).push(statement);
    }
  }
  while (segments.length > 1 && segments.at(-1).length === 0) segments.pop();
  return segments;
}

function canonicalizeSegments(runtime, body) {
  return splitSegments(body, runtime).map((segment) => {
    const scope = new CanonicalScope();
    scope.bind(runtime.instructionPointName, "IP");
    scope.bind(runtime.opcodeName, "OP");
    if (runtime.upvaluesName) scope.bind(runtime.upvaluesName, "U");
    if (runtime.environmentName) scope.bind(runtime.environmentName, "ENV");
    if (runtime.factoryName) scope.bind(runtime.factoryName, "WRAP");
    if (runtime.topName) scope.bind(runtime.topName, "TOP");
    if (runtime.stackName) scope.bind(runtime.stackName, "R");
    scope.bind(runtime.instructionName, "I");
    return segment.map((statement) => printStatement(statement, scope, runtime)).join(";");
  });
}

export function analyzeWrapper(source) {
  const parseSource = source
    .replace(/&lt;/g, "<")
    .replace(/&gt;/g, ">")
    .replace(/&amp;/g, "&")
    .replace(
    /\b([A-Za-z_][A-Za-z0-9_]*)\s*(\.\.|\+|-|\*|\/|%|\^)=/g,
    (_, name, operator) => `${name}=${name}${operator}`
    );
  let ast;
  try {
    ast = luaparse.parse(parseSource, {
      luaVersion: "5.2",
      ranges: true,
      encodingMode: "x-user-defined"
    });
  } catch {
    throw new Error("Input does not contain a valid PSU payload");
  }

  const { wrapper, environment } = seedTopEnvironment(ast);
  const tags = findConstantTags(wrapper, environment);
  const runtime = findRuntime(wrapper, environment);
  const handlers = new Map();

  for (let opcode = 0; opcode <= 255; opcode += 1) {
    const body = resolveHandlerBody(runtime, opcode);
    if (!body || body.length === 0) continue;
    handlers.set(opcode, {
      opcode,
      segments: canonicalizeSegments(runtime, body)
    });
  }

  return {
    tags,
    runtime: {
      enumKey: runtime.enumKey,
      nextKey: runtime.nextKey,
      stackName: runtime.stackName,
      upvaluesName: runtime.upvaluesName
    },
    handlers
  };
}
