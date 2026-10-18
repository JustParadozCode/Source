<div align="center">

# PSU-Deobfuscator

### Static Devirtualizer for the Ol' Pal PSU

![Node.js](https://img.shields.io/badge/Node.js-18%2B-339933?style=flat-square&logo=nodedotjs&logoColor=white)
![Lua](https://img.shields.io/badge/Lua-5.1-2C2D72?style=flat-square&logo=lua&logoColor=white)
![Mode](https://img.shields.io/badge/mode-static-black?style=flat-square)

<sub>Recover the VM. Lift the instructions. Get readable Lua back.</sub>

</div>

---

PSU-Deobfuscator statically recovers PSU's packed VM, identifies its handlers, expands superinstructions, lowers the recovered program back to Lua 5.1 bytecode, and cleans up the resulting source.

No runtime execution is required for the lifting stage.

## How it works

```text
obfuscated Lua
      │
      ▼
 wrapper / payload recovery
      │
      ▼
 VM + handler identification
      │
      ▼
 superinstruction expansion
      │
      ▼
 semantic lifting
      │
      ▼
 Lua 5.1 bytecode
      │
      ▼
     unluac
      │
      ▼
 source cleanup
      │
      ▼
 readable Lua
```

> [!NOTE]
> The decoder is intentionally conservative. If a handler or operand layout cannot be recovered with enough confidence, it fails instead of inventing bytecode.

## Requirements

| Requirement | Notes |
|---|---|
| **Node.js 18+** | Required |
| **Java** | Required for source output through unluac |
| **`unluac.jar`** | Place it in `tools/unluac.jar` or provide a custom path |

Install dependencies:

```bash
npm install
```

Place unluac here:

```text
tools/unluac.jar
```

Or point to it manually with `--unluac` or `UNLUAC_JAR`.

---

## Usage

### Deobfuscate a file

```bash
npm run deobf -- input.lua
```

Default output:

```text
input.deobf.lua
```

### Choose an output file

```bash
npm run deobf -- input.lua -o output.lua
```

### Recover bytecode only

```bash
npm run deobf -- input.lua --bytecode output.luac
```

### Run the decoder/lifter only

Useful when you want to test recovery without invoking unluac.

```bash
npm run deobf -- input.lua --check
```

### Keep raw unluac output

Skips the final cleanup pass.

```bash
npm run deobf -- input.lua --raw
```

### Print recovery information

```bash
npm run deobf -- input.lua --json
```

### Use a custom unluac path

```bash
npm run deobf -- input.lua --unluac /path/to/unluac.jar
```

<details>
<summary><b>Quick command reference</b></summary>

<br>

| Command | Purpose |
|---|---|
| `npm run deobf -- input.lua` | Full deobfuscation |
| `-o output.lua` | Custom source output |
| `--bytecode output.luac` | Emit Lua 5.1 bytecode |
| `--check` | Decode/lift only |
| `--raw` | Skip source cleanup |
| `--json` | Print recovery data |
| `--unluac <path>` | Use another unluac JAR |

</details>

---

## Project structure

```text
src/
├── bytecode/       Lua 5.1 chunk writer
├── core/           wrapper parsing, payload decoding and VM recovery
├── decompile/      unluac integration
├── emit/           source cleanup
├── ir/             semantic recovery and lowering
├── cli.js
├── deobfuscator.js
└── testSamples.js

tools/              external tooling
```

<details>
<summary><b>Pipeline notes</b></summary>

<br>

The project keeps the VM recovery and source decompilation stages separate.

That means the actual decoder/lifter can be tested without Java or unluac, while the final source stage can still take advantage of unluac when readable Lua output is wanted.

Recovered temporary locals are normalized to simple names such as:

```lua
v1
v2
v3
```

instead of preserving noisy decompiler-generated identifiers.

</details>

---

## Credits

**LSD** — original work and research this project builds on.

**unluac** — Lua 5.1 bytecode decompiler used for source recovery.

---

<div align="center">

<sub>PSU goes in. Lua comes out.</sub>

</div>
