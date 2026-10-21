#!/usr/bin/env python3
"""The model of a tool's parameter schema, shared by the tools that read it.

WHAT THIS IS
------------
  A builtin tool declares the JSON Schema of its arguments once, as
  [ToolParam] attributes on the tool class (issue #1780).  The schema is the
  tool's contract with the model, so it may not drift by a byte, and the
  product used to carry that contract twice: once as a hand-written JSON
  literal in every tool, and once more in the test project's golden file.

  This module is the ONE reading of that declaration.  Two consumers use it:

    tools/codemod-tool-schemas.py  the one-shot migration, and the writer of
                                    the pin file;
    tools/check-tool-schema-pin.py the gate that says the pin still agrees
                                    with the declarations.

  The compiler has the third reader: Harbor.CodeGen.ToolSchemaGenerator
  renders the same declaration and compares its SHA-256 with the same pin.
  A gate that only one of them could satisfy would be a gate with a hole,
  so the pin is the shared artefact: if the two renderings ever disagreed,
  the build would fail, and the gate would fail with it.

THE CANONICAL FORM
------------------
  The canonical form of a schema is its compact JSON text, with the keys in
  declaration order and the strings escaped the way
  json.dumps(..., ensure_ascii=False) escapes them.  It is a lossless
  encoding of the schema's CONTENT, which is what the contract is: the
  whitespace of the literal that carries it is not part of it, and the
  bytes the model receives are the canonical form written through
  JsonElement.WriteTo.

  The pin is the SHA-256 of that text, per tool, keyed by the tool's CLASS
  name: McpToolAdapter's tool name is derived from the MCP server and the
  tool it wraps, so it is not stable.
"""
from __future__ import annotations

import hashlib
import json
import os
import re

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TOOLS_ROOT = os.path.join(REPO_ROOT, "src", "Harbor.Tools.Builtin", "Tools")
SCHEMAS_ROOT = os.path.join(REPO_ROOT, "src", "Harbor.Tools.Builtin", "Schemas")
PIN_PATH = os.path.join(SCHEMAS_ROOT, "tool-schemas.sha256.json")

# The marker and the parameter attribute, matched by name in the source.
SCHEMA_MARKER = "[ToolSchema]"

# The legacy literal: a static JsonDocument field built from a raw string.
LEGACY_FIELD_RE = re.compile(
    r"[ \t]*/// <summary>\n"
    r"[ \t]*///     The tool's parameter schema\.[^\n]*\n"
    r"(?:[ \t]*///[^\n]*\n)*"
    r"[ \t]*/// </summary>\n"
    r"[ \t]*private static readonly JsonDocument Schema = JsonDocument\.Parse\(\"\"\"\n"
    r"(?P<body>.*?)"
    r"[ \t]*\"\"\"\);\n"
    r"[ \t]*/// <inheritdoc />\n"
    r"[ \t]*public JsonDocument ParameterSchema => Schema;\n"
    r"\n",
    re.DOTALL,
)

# A tool class. The anchor keeps a class declaration inside a doc comment
# (the example in ToolParamAttribute.cs) out of the result.
TOOL_CLASS_RE = re.compile(r"^public sealed (partial )?class (\w+) : ITool", re.MULTILINE)

# The pieces of a [ToolParam(...)] argument list.
DESCRIPTION_RE = re.compile(r"Description = \"(?P<value>(?:[^\"\\]|\\.)*)\"")
ENUM_RE = re.compile(r"Enum = new\[\] \{ (?P<value>[^}]*) \}")
ITEMS_OF_RE = re.compile(r"ItemsOf = \"(?P<value>[^\"]*)\"")
REQUIRED_RE = re.compile(r"Required = true")

# The parameter types the declaration may use.  A type outside this set is
# a typo, and a typo that reaches the model is a wrong tool call.
KNOWN_TYPES = ("string", "integer", "boolean", "object", "array")


class Failure(Exception):
    """A refusal: the tree does not say what this tool expects it to say."""


# ---------------------------------------------------------------- the legacy form


def dedent_raw_string(body: str) -> str:
    """The text of a C# raw string literal, with the common indentation gone.

    A raw string literal strips the indentation of its closing delimiter
    from every line.  The legacy literals are indented to the column of the
    code that holds them, so the text the runtime saw is the relative
    indentation only.
    """
    lines = body.split("\n")
    if not lines:
        return ""
    closing = lines[-1]
    indent = len(closing) - len(closing.lstrip(" "))
    out = []
    for line in lines[:-1]:
        stripped = line[:indent]
        if stripped.strip():
            raise Failure(f"a raw string line is indented less than its closing delimiter: {line!r}")
        out.append(line[indent:])
    return "\n".join(out)


def extract_legacy(path: str) -> tuple[str, dict] | None:
    """The class name and the schema of the legacy literal in one file."""
    with open(path, encoding="utf-8") as handle:
        text = handle.read()
    match = LEGACY_FIELD_RE.search(text)
    if not match:
        return None
    class_match = TOOL_CLASS_RE.search(text)
    if not class_match:
        raise Failure(f"{path}: a legacy schema without a tool class")
    schema = json.loads(dedent_raw_string(match.group("body")))
    return class_match.group(2), schema


# ---------------------------------------------------------------- the canonical form


def canonical(schema: dict) -> str:
    """The canonical text of a schema: compact JSON, declaration order."""
    return json.dumps(schema, ensure_ascii=False, separators=(",", ":"))


def digest(schema: dict) -> str:
    """The SHA-256 of the canonical text, lower-case hex."""
    return hashlib.sha256(canonical(schema).encode("utf-8")).hexdigest()


def digest_text(text: str) -> str:
    """The SHA-256 of a text, lower-case hex."""
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


# ---------------------------------------------------------------- the declaration


def escape_cs(value: str) -> str:
    """A C# string literal body for a value that may hold quotes."""
    out = []
    for char in value:
        if char == "\\":
            out.append("\\\\")
        elif char == '"':
            out.append('\\"')
        elif char == "\n":
            out.append("\\n")
        elif char == "\r":
            out.append("\\r")
        elif char == "\t":
            out.append("\\t")
        else:
            out.append(char)
    return "".join(out)


def unescape_cs(value: str) -> str:
    """The value of a C# string literal body, read back."""
    escapes = {"n": "\n", "r": "\r", "t": "\t", '"': '"', "\\": "\\"}
    out = []
    index = 0
    while index < len(value):
        char = value[index]
        if char == "\\" and index + 1 < len(value) and value[index + 1] in escapes:
            out.append(escapes[value[index + 1]])
            index += 2
            continue
        out.append(char)
        index += 1
    return "".join(out)


def scan_arguments(text: str, start: int) -> tuple[str, int]:
    """The argument list of an attribute, from the opening parenthesis.

    A description may hold a bracket, a quote or a brace, so the scan has
    to know what a string literal is: a regex over the arguments would stop
    at the first bracket inside a description and read the rest of the
    declaration wrongly.
    """
    index = start
    depth = 0
    in_string = False
    escaped = False
    out: list[str] = []
    while index < len(text):
        char = text[index]
        if in_string:
            out.append(char)
            if escaped:
                escaped = False
            elif char == "\\":
                escaped = True
            elif char == '"':
                in_string = False
            index += 1
            continue
        if char == '"':
            in_string = True
            out.append(char)
            index += 1
            continue
        if char in "{[":
            depth += 1
        elif char in "}]":
            depth -= 1
        elif char == ")" and depth == 0:
            return "".join(out), index + 1
        out.append(char)
        index += 1
    return "".join(out), index


def split_arguments(arguments: str) -> list[str]:
    """The comma-separated arguments of an attribute, strings kept whole."""
    parts: list[str] = []
    current: list[str] = []
    depth = 0
    in_string = False
    escaped = False
    for char in arguments:
        if in_string:
            current.append(char)
            if escaped:
                escaped = False
            elif char == "\\":
                escaped = True
            elif char == '"':
                in_string = False
            continue
        if char == '"':
            in_string = True
            current.append(char)
            continue
        if char in "{[":
            depth += 1
        elif char in "}]":
            depth -= 1
        elif char == "," and depth == 0:
            parts.append("".join(current))
            current = []
            continue
        current.append(char)
    parts.append("".join(current))
    return parts


def parse_params(text: str) -> list[dict]:
    """Every [ToolParam(...)] declaration in a text, as a dict each."""
    found: list[dict] = []
    index = 0
    while True:
        start = text.find("[ToolParam(", index)
        if start < 0:
            return found
        arguments, index = scan_arguments(text, start + len("[ToolParam("))
        parts = split_arguments(arguments)
        if len(parts) < 2:
            raise Failure(f"a [ToolParam] declaration without a name and a type: {arguments!r}")
        param = {
            "name": unescape_cs(parts[0].strip()[1:-1]),
            "type": unescape_cs(parts[1].strip()[1:-1]),
            "description": None,
            "enum": None,
            "required": False,
            "items_of": None,
        }
        rest = " ".join(parts[2:])
        description = DESCRIPTION_RE.search(rest)
        if description:
            param["description"] = unescape_cs(description.group("value"))
        enum = ENUM_RE.search(rest)
        if enum:
            param["enum"] = [
                unescape_cs(value.strip().strip('"'))
                for value in enum.group("value").split(",")
                if value.strip()
            ]
        items_of = ITEMS_OF_RE.search(rest)
        if items_of:
            param["items_of"] = unescape_cs(items_of.group("value"))
        param["required"] = bool(REQUIRED_RE.search(rest))
        found.append(param)


def attribute_lines(schema: dict) -> list[str]:
    """The [ToolParam] lines that declare one schema."""
    properties = schema.get("properties", {})
    required = list(schema.get("required", []))
    lines: list[str] = []

    for name, spec in properties.items():
        if spec.get("type", "string") not in KNOWN_TYPES:
            raise Failure(f"property '{name}' has the unknown type {spec.get('type')!r}")
        args = [f'"{escape_cs(name)}"', f'"{escape_cs(spec.get("type", "string"))}"']
        if "enum" in spec:
            values = ", ".join(f'"{escape_cs(v)}"' for v in spec["enum"])
            args.append(f"Enum = new[] {{ {values} }}")
        if "description" in spec:
            args.append(f'Description = "{escape_cs(spec["description"])}"')
        if name in required:
            args.append("Required = true")
        lines.append(f"[ToolParam({', '.join(args)})]")

        items = spec.get("items")
        if items is None:
            continue
        if spec.get("type") != "array":
            raise Failure(f"property '{name}' has items but is not an array")
        item_required = list(items.get("required", []))
        for item_name, item_spec in items.get("properties", {}).items():
            item_args = [
                f'"{escape_cs(item_name)}"',
                f'"{escape_cs(item_spec.get("type", "string"))}"',
                f'ItemsOf = "{escape_cs(name)}"',
            ]
            if "description" in item_spec:
                item_args.append(f'Description = "{escape_cs(item_spec["description"])}"')
            if item_name in item_required:
                item_args.append("Required = true")
            lines.append(f"[ToolParam({', '.join(item_args)})]")

    return lines


def read_declaration(path: str) -> tuple[str, dict] | None:
    """The schema a tool file declares, read back from its attributes."""
    with open(path, encoding="utf-8") as handle:
        text = handle.read()
    if SCHEMA_MARKER not in text:
        return None

    class_match = TOOL_CLASS_RE.search(text)
    if not class_match:
        return None

    params = parse_params(text)
    if not params:
        raise Failure(f"{path}: a declared schema without a parameter")

    root: dict[str, dict] = {}
    items: dict[str, dict[str, dict]] = {}
    root_required: list[str] = []
    item_required: dict[str, list[str]] = {}

    for param in params:
        name = param["name"]
        spec: dict = {"type": param["type"]}
        if spec["type"] not in KNOWN_TYPES:
            raise Failure(f"{path}: parameter '{name}' has the unknown type {spec['type']!r}")
        if param["enum"] is not None:
            spec["enum"] = param["enum"]
        if param["description"] is not None:
            spec["description"] = param["description"]

        parent = param["items_of"]
        if parent is not None:
            items.setdefault(parent, {})[name] = spec
            if param["required"]:
                item_required.setdefault(parent, []).append(name)
            continue

        root[name] = spec
        if param["required"]:
            root_required.append(name)

    schema: dict = {"type": "object", "properties": root}
    if root_required:
        schema["required"] = root_required
    for parent, properties in items.items():
        spec = root[parent]
        item: dict = {"type": "object", "properties": properties}
        required_names = item_required.get(parent)
        if required_names:
            item["required"] = required_names
        spec["items"] = item
    return class_match.group(2), schema


# ---------------------------------------------------------------- the tree


def tool_files() -> list[str]:
    """Every source file under the builtin tools' directory."""
    found: list[str] = []
    for base, _dirs, files in os.walk(TOOLS_ROOT):
        for name in sorted(files):
            if name.endswith(".cs"):
                found.append(os.path.join(base, name))
    return found


def collect() -> list[tuple[str, dict]]:
    """Every tool that declares a schema, with that schema."""
    found: list[tuple[str, dict]] = []
    for path in tool_files():
        legacy = extract_legacy(path)
        if legacy is not None:
            found.append(legacy)
            continue
        declared = read_declaration(path)
        if declared is not None:
            found.append(declared)
    found.sort(key=lambda item: item[0])

    names = [name for name, _schema in found]
    if len(set(names)) != len(names):
        duplicates = sorted({name for name in names if names.count(name) > 1})
        raise Failure(f"two tools declare the same name: {', '.join(duplicates)}")
    return found


def read_pins() -> dict[str, str]:
    """The pin file: one hash per tool."""
    if not os.path.isfile(PIN_PATH):
        return {}
    with open(PIN_PATH, encoding="utf-8") as handle:
        text = handle.read()
    pins: dict[str, str] = {}
    index = 0
    while index < len(text):
        open_quote = text.find('"', index)
        if open_quote < 0:
            break
        close_quote = text.find('"', open_quote + 1)
        if close_quote < 0:
            break
        key = text[open_quote + 1:close_quote]
        colon = text.find(":", close_quote)
        if colon < 0:
            break
        value_open = text.find('"', colon)
        if value_open < 0:
            break
        value_close = text.find('"', value_open + 1)
        if value_close < 0:
            break
        pins[key] = text[value_open + 1:value_close]
        index = value_close + 1
    return pins


def pin_text(entries: list[tuple[str, dict]]) -> str:
    """The pin file's text for a set of tools."""
    lines = ["{"]
    for index, (class_name, schema) in enumerate(entries):
        comma = "," if index < len(entries) - 1 else ""
        lines.append(f'  "{class_name}": "{digest(schema)}"{comma}')
    lines.append("}")
    return "\n".join(lines) + "\n"


def write_pin(entries: list[tuple[str, dict]]) -> None:
    """Write the pin file: one hash per tool."""
    os.makedirs(SCHEMAS_ROOT, exist_ok=True)
    with open(PIN_PATH, "w", encoding="utf-8") as handle:
        handle.write(pin_text(entries))


# ---------------------------------------------------------------- the migration


def migrate_file(path: str) -> tuple[str, str] | None:
    """Rewrite one tool file: the literal becomes attributes."""
    with open(path, encoding="utf-8") as handle:
        text = handle.read()

    match = LEGACY_FIELD_RE.search(text)
    if not match:
        return None
    class_match = TOOL_CLASS_RE.search(text)
    if not class_match:
        raise Failure(f"{path}: a legacy schema without a tool class")
    class_name = class_match.group(2)

    schema = json.loads(dedent_raw_string(match.group("body")))
    lines = attribute_lines(schema)

    # The declaration goes above the class, next to [ToolInfo].
    original = class_match.group(0)
    class_decl = original
    if "partial" not in class_decl:
        class_decl = class_decl.replace("public sealed class", "public sealed partial class")
    text = text.replace(original, class_decl, 1)

    marker = SCHEMA_MARKER + "\n" + "".join(line + "\n" for line in lines)
    text = text.replace(class_decl, marker + class_decl, 1)

    # The literal and the property it fed go away.
    text = LEGACY_FIELD_RE.sub("", text, count=1)

    # A tool that lives in another namespace needs the attributes in scope.
    if "namespace Harbor.Tools.Builtin;" not in text and "using Harbor.Tools.Builtin;" not in text:
        text = insert_using(text, "using Harbor.Tools.Builtin;")

    with open(path, "w", encoding="utf-8") as handle:
        handle.write(text)
    return class_name, digest(schema)


def insert_using(text: str, statement: str) -> str:
    """Put a using directive where the file's other ones say it belongs."""
    lines = text.split("\n")
    index = 0
    for position, line in enumerate(lines):
        if not line.startswith("using "):
            continue
        if line > statement:
            index = position
            break
        index = position + 1

    lines.insert(index, statement)
    return "\n".join(lines)
