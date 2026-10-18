export function identifierName(node) {
  return node?.type === "Identifier" ? node.name : null;
}

export function walk(node, visitor, parent = null) {
  if (!node || typeof node !== "object") return;
  visitor(node, parent);
  for (const [key, value] of Object.entries(node)) {
    if (key === "loc" || key === "range") continue;
    if (Array.isArray(value)) {
      for (const child of value) walk(child, visitor, node);
    } else {
      walk(value, visitor, node);
    }
  }
}

function luaTruthy(value) {
  return value !== false && value !== null && value !== undefined;
}

function tableLength(value) {
  if (Array.isArray(value)) return value.length;
  if (value instanceof Map) {
    let length = 0;
    while (value.has(length + 1)) length += 1;
    return length;
  }
  if (typeof value === "string") return value.length;
  throw new Error(`Unsupported length operand ${typeof value}`);
}

function firstValue(value) {
  return value?.multiple === true ? value.values[0] : value;
}

function staticClosure(declaration, environment) {
  return {
    staticFunction: true,
    declaration,
    environment: new Map(environment)
  };
}

function evaluateFunctionCall(node, environment) {
  let callable = node.base?.type === "FunctionDeclaration"
    ? staticClosure(node.base, environment)
    : firstValue(evaluateStatic(node.base, environment));
  let closureEnvironment = environment;
  let declaration = callable;
  if (callable?.staticFunction === true) {
    declaration = callable.declaration;
    closureEnvironment = callable.environment;
  }
  if (declaration?.type !== "FunctionDeclaration") {
    throw new Error(`Unsupported static call ${node.base?.type}:${node.base?.name ?? ""}`);
  }
  const localEnvironment = new Map(environment);
  if (callable?.staticFunction === true) {
    for (const [name, value] of closureEnvironment) {
      localEnvironment.set(name, value);
    }
  }
  const argumentValues = node.arguments.map((argument) =>
    firstValue(evaluateStatic(argument, environment))
  );
  const varargs = [];
  let argumentIndex = 0;
  for (const parameter of declaration.parameters) {
    if (parameter.type === "VarargLiteral") {
      varargs.push(...argumentValues.slice(argumentIndex));
      break;
    }
    localEnvironment.set(parameter.name, argumentValues[argumentIndex] ?? null);
    argumentIndex += 1;
  }
  localEnvironment.set("...", { multiple: true, values: varargs });

  function execute(statements, scope) {
    for (const statement of statements) {
      if (statement.type === "LocalStatement") {
        const values = [];
        for (let index = 0; index < statement.init.length; index += 1) {
          const value = evaluateStatic(statement.init[index], scope);
          if (value?.multiple === true && index === statement.init.length - 1) {
            values.push(...value.values);
          } else {
            values.push(firstValue(value));
          }
        }
        for (let index = 0; index < statement.variables.length; index += 1) {
          const name = identifierName(statement.variables[index]);
          if (name) scope.set(name, values[index] ?? null);
        }
        continue;
      }
      if (statement.type === "AssignmentStatement") {
        const values = [];
        for (let index = 0; index < statement.init.length; index += 1) {
          const value = evaluateStatic(statement.init[index], scope);
          if (value?.multiple === true && index === statement.init.length - 1) {
            values.push(...value.values);
          } else {
            values.push(firstValue(value));
          }
        }
        for (let index = 0; index < statement.variables.length; index += 1) {
          const variable = statement.variables[index];
          const value = values[index] ?? null;
          if (variable.type === "Identifier") {
            scope.set(variable.name, value);
          } else if (variable.type === "IndexExpression") {
            const base = firstValue(evaluateStatic(variable.base, scope));
            const key = firstValue(evaluateStatic(variable.index, scope));
            if (base instanceof Map) base.set(key, value);
            else if (base && typeof base === "object") base[key] = value;
            else throw new Error("Unsupported static indexed assignment");
          } else {
            throw new Error("Unsupported static assignment");
          }
        }
        continue;
      }
      if (statement.type === "IfStatement") {
        let selected = null;
        for (const clause of statement.clauses) {
          if (!clause.condition || luaTruthy(firstValue(evaluateStatic(clause.condition, scope)))) {
            selected = clause.body;
            break;
          }
        }
        if (selected) {
          const result = execute(selected, scope);
          if (result?.returned || result?.break) return result;
        }
        continue;
      }
      if (statement.type === "WhileStatement") {
        let iterations = 0;
        while (luaTruthy(firstValue(evaluateStatic(statement.condition, scope)))) {
          iterations += 1;
          if (iterations > 100_000) throw new Error("Static helper loop did not terminate");
          const result = execute(statement.body, scope);
          if (result?.returned) return result;
          if (result?.break) break;
        }
        continue;
      }
      if (statement.type === "DoStatement") {
        const result = execute(statement.body, scope);
        if (result?.returned || result?.break) return result;
        continue;
      }
      if (statement.type === "BreakStatement") {
        return { returned: false, break: true, value: null };
      }
      if (statement.type !== "ReturnStatement") {
        throw new Error(`Unsupported static function statement ${statement.type}`);
      }

      const values = [];
      for (let index = 0; index < statement.arguments.length; index += 1) {
        const value = evaluateStatic(statement.arguments[index], scope);
        if (value?.multiple === true && index === statement.arguments.length - 1) {
          values.push(...value.values);
        } else {
          values.push(firstValue(value));
        }
      }
      return { returned: true, break: false, value: { multiple: true, values } };
    }
    return { returned: false, break: false, value: null };
  }

  return execute(declaration.body, localEnvironment).value;
}

function evaluateTable(node, environment) {
  const table = new Map();
  let arrayIndex = 1;

  for (let fieldIndex = 0; fieldIndex < node.fields.length; fieldIndex += 1) {
    const field = node.fields[fieldIndex];
    if (field.type === "TableKey") {
      const key = firstValue(evaluateStatic(field.key, environment));
      table.set(key, firstValue(evaluateStatic(field.value, environment)));
      continue;
    }
    if (field.type === "TableKeyString") {
      table.set(field.key.name, firstValue(evaluateStatic(field.value, environment)));
      continue;
    }

    const value = evaluateStatic(field.value, environment);
    if (value?.multiple === true && fieldIndex === node.fields.length - 1) {
      for (const item of value.values) table.set(arrayIndex++, item);
    } else {
      table.set(arrayIndex++, firstValue(value));
    }
  }

  return table;
}

export function evaluateStatic(node, environment = new Map()) {
  if (!node) throw new Error("Missing expression");

  switch (node.type) {
    case "NumericLiteral":
    case "StringLiteral":
    case "BooleanLiteral":
      return node.value;
    case "NilLiteral":
      return null;
    case "VarargLiteral":
      if (environment.has("...")) return environment.get("...");
      return { multiple: true, values: [] };
    case "Identifier":
      if (environment.has(node.name)) return environment.get(node.name);
      throw new Error(`Unknown identifier ${node.name}`);
    case "TableConstructorExpression":
      return evaluateTable(node, environment);
    case "FunctionDeclaration":
      return staticClosure(node, environment);
    case "IndexExpression": {
      const base = firstValue(evaluateStatic(node.base, environment));
      const key = firstValue(evaluateStatic(node.index, environment));
      if (base instanceof Map) return base.get(key);
      if (Array.isArray(base)) return base[key];
      if (base && typeof base === "object") return base[key];
      throw new Error("Unsupported static index");
    }
    case "MemberExpression": {
      const base = firstValue(evaluateStatic(node.base, environment));
      if (base instanceof Map) return base.get(node.identifier.name);
      return base?.[node.identifier.name];
    }
    case "CallExpression":
      return evaluateFunctionCall(node, environment);
    case "UnaryExpression": {
      const value = firstValue(evaluateStatic(node.argument, environment));
      if (node.operator === "-") return -value;
      if (node.operator === "+") return +value;
      if (node.operator === "not") return !luaTruthy(value);
      if (node.operator === "#") return tableLength(value);
      throw new Error(`Unsupported unary operator ${node.operator}`);
    }
    case "LogicalExpression": {
      let left;
      try {
        left = firstValue(evaluateStatic(node.left, environment));
      } catch (error) {
        if (node.operator === "or") {
          return firstValue(evaluateStatic(node.right, environment));
        }
        throw error;
      }
      if (node.operator === "and") {
        return luaTruthy(left) ? firstValue(evaluateStatic(node.right, environment)) : left;
      }
      if (node.operator === "or") {
        return luaTruthy(left) ? left : firstValue(evaluateStatic(node.right, environment));
      }
      throw new Error(`Unsupported logical operator ${node.operator}`);
    }
    case "BinaryExpression": {
      const left = firstValue(evaluateStatic(node.left, environment));
      const right = firstValue(evaluateStatic(node.right, environment));
      switch (node.operator) {
        case "+": return left + right;
        case "-": return left - right;
        case "*": return left * right;
        case "/": return left / right;
        case "%": return left % right;
        case "^": return left ** right;
        case "==": return left === right;
        case "~=": return left !== right;
        case "<": return left < right;
        case "<=": return left <= right;
        case ">": return left > right;
        case ">=": return left >= right;
        default: throw new Error(`Unsupported binary operator ${node.operator}`);
      }
    }
    default:
      throw new Error(`Unsupported expression ${node.type}`);
  }
}

export function seedTopEnvironment(ast) {
  const topReturn = ast.body.find((statement) => statement.type === "ReturnStatement");
  let call = topReturn?.arguments?.[0];
  if (call?.type !== "CallExpression" || call.base?.type !== "FunctionDeclaration") {
    const calls = [];
    walk(ast, (node) => {
      if (
        node.type === "CallExpression" &&
        node.base?.type === "FunctionDeclaration" &&
        node.arguments.some((argument) => argument.type === "TableConstructorExpression")
      ) {
        calls.push(node);
      }
    });
    calls.sort((left, right) =>
      (right.base.range?.[1] - right.base.range?.[0]) -
      (left.base.range?.[1] - left.base.range?.[0])
    );
    call = calls[0];
  }
  const wrapper = call?.base;
  if (call?.type !== "CallExpression" || wrapper?.type !== "FunctionDeclaration") {
    throw new Error("Invalid PSU wrapper");
  }

  const environment = new Map();
  for (let index = 0; index < wrapper.parameters.length; index += 1) {
    const name = identifierName(wrapper.parameters[index]);
    const argument = call.arguments[index];
    if (!name || !argument || argument.type === "VarargLiteral") continue;
    environment.set(name, firstValue(evaluateStatic(argument, environment)));
  }

  for (const statement of wrapper.body) {
    if (statement.type === "FunctionDeclaration" && statement.identifier?.name) {
      environment.set(
        statement.identifier.name,
        staticClosure(statement, environment)
      );
      continue;
    }
    if (statement.type !== "LocalStatement") continue;
    for (let index = 0; index < statement.variables.length; index += 1) {
      const name = identifierName(statement.variables[index]);
      const init = statement.init[index];
      if (!name || !init) continue;
      try {
        environment.set(name, firstValue(evaluateStatic(init, environment)));
      } catch {
      }
    }
  }

  return { wrapper, environment };
}

export function extendStaticEnvironment(body, parent = new Map()) {
  const environment = new Map(parent);
  for (const statement of body) {
    if (statement.type === "FunctionDeclaration" && statement.identifier?.name) {
      environment.set(
        statement.identifier.name,
        staticClosure(statement, environment)
      );
      continue;
    }
    if (statement.type !== "LocalStatement") continue;
    const pending = [];
    for (let index = 0; index < statement.variables.length; index += 1) {
      const name = identifierName(statement.variables[index]);
      const init = statement.init[index];
      if (!name || !init) continue;
      try {
        pending.push([name, firstValue(evaluateStatic(init, environment))]);
      } catch {
      }
    }
    for (const [name, value] of pending) environment.set(name, value);
  }
  return environment;
}
