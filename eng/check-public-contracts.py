#!/usr/bin/env python3
"""Report Contracts types unreachable from the public client's C# source.

This is a lexical check, not a C# compiler. Comments and literals do not count as
references, nested declarations are not top-level types, and generated build
output is ignored. Dependencies are followed per declaration (not per file), so
an unused group cannot keep itself public through a reference cycle.
"""

from pathlib import Path
import re
import sys


ROOT = Path(__file__).resolve().parents[1]
CLIENTS = (
    "src/MessageBus.Cli", "src/MessageBus.Mcp", "src/MessageBus.Runner.Transport",
    "src/MessageBus.Attachments.Client", "src/MessageBus.Crypto",
    "tests/MessageBus.Cli.Tests", "tests/MessageBus.Contracts.Tests",
    "tests/MessageBus.Crypto.Tests",
)
# RefKinds is the wire reference vocabulary, used by the split MCP server.
# The JSON context is serializer infrastructure, not a contract dependency root:
# its JsonSerializable attributes must never keep unused DTOs public.
# FrictionTask is already on the public serialization surface. Keep it until
# a coordinated protocol change can retire it.
ALLOW = {"RefKinds", "MessageBusJsonContext", "FrictionTask"}
# Exhaustive sample catalogs enumerate every DTO and cannot prove client usage.
REFERENCE_EXCLUSIONS = {"tests/MessageBus.Contracts.Tests/ContractSamples.cs"}
RAW_STRING = re.compile(r'\$*("{3,})')
LITERAL = re.compile(r'''\$@"|@\$"|\$"|@"|"|' ''', re.VERBOSE)
DECLARATION = re.compile(
    r"\bpublic\s+(?:(?:static|sealed|abstract|partial|readonly|ref|unsafe)\s+)*"
    r"(?:(?:record(?:\s+(?:class|struct))?|class|struct|enum|interface)\s+(\w+)"
    r"|delegate\s+[^;{}=]+?\s+(\w+)\s*(?=[<(]))")


def code(text):
    """Blank comments/literals, retaining code inside interpolated strings."""
    result = list(text)

    def blank(start, end):
        for pos in range(start, end):
            if text[pos] != "\n":
                result[pos] = " "

    def scan(pos, interpolation=False):
        depth = 0
        while pos < len(text):
            start = pos
            if text.startswith("//", pos):
                end = text.find("\n", pos)
                pos = len(text) if end < 0 else end
            elif text.startswith("/*", pos):
                end = text.find("*/", pos + 2)
                pos = len(text) if end < 0 else end + 2
            elif raw := RAW_STRING.match(text, pos):
                delimiter = raw[1]
                end = text.find(delimiter, raw.end())
                pos = len(text) if end < 0 else end + len(delimiter)
            elif literal := LITERAL.match(text, pos):
                prefix = literal[0]
                quote = prefix[-1]
                verbatim = "@" in prefix
                interpolated = "$" in prefix
                pos += len(prefix)
                while pos < len(text):
                    if not verbatim and text[pos] == "\\":
                        pos += 2
                    elif verbatim and text.startswith('""', pos):
                        pos += 2
                    elif text[pos] == quote:
                        pos += 1
                        break
                    elif interpolated and text.startswith("{{", pos):
                        pos += 2
                    elif interpolated and text[pos] == "{":
                        blank(start, pos + 1)
                        pos = scan(pos + 1, interpolation=True)
                        start = pos
                        pos += 1
                    else:
                        pos += 1
            else:
                if interpolation:
                    if text[pos] == "}" and depth == 0:
                        return pos
                    depth += (text[pos] == "{") - (text[pos] == "}")
                pos += 1
                continue
            blank(start, min(pos, len(text)))
        return pos

    scan(0)
    return "".join(result)


def sources(folder):
    return (p for p in sorted((ROOT / folder).rglob("*.cs"))
            if not {"obj", "bin", "node_modules", ".git"}.intersection(p.parts))


def declarations(text):
    """Yield (name, declaration start, end) for public namespace-level types."""
    clean = code(text)
    for match in DECLARATION.finditer(clean):
        name = match[1] or match[2]
        prefix = clean[:match.start()]
        # Both file-scoped and block-scoped namespaces are supported.
        stack = []
        for token in re.finditer(r"\bnamespace\s+[\w.]+\s*{|[{}]", prefix):
            if token[0].endswith("{"):
                stack.append(token[0].startswith("namespace"))
            elif stack:
                stack.pop()
        namespace_depth = sum(stack)
        if prefix.count("{") - prefix.count("}") != namespace_depth:
            continue
        pos = match.end()
        parens = 0
        while pos < len(clean):
            char = clean[pos]
            if char == "(":
                parens += 1
            elif char == ")":
                parens -= 1
            elif not parens and char in "{;":
                break
            pos += 1
        if pos == len(clean):
            raise ValueError(f"Unterminated declaration: {name}")
        if clean[pos] == "{":
            depth = 1
            pos += 1
            while depth:
                depth += (clean[pos] == "{") - (clean[pos] == "}")
                pos += 1
        else:
            pos += 1
        yield name, match.start(), pos


def unused_types():
    types = {}
    for path in sources("src/MessageBus.Contracts"):
        text = path.read_text(encoding="utf-8-sig")
        clean = code(text)
        previous_end = 0
        for name, start, end in declarations(text):
            if name in types:
                raise ValueError(f"Ambiguous contract name: {name}")
            # Attributes such as JsonDerivedType are dependencies of their type.
            attributes = clean[previous_end:start]
            attribute_start = attributes.find("[")
            dependencies = clean[start:end]
            if attribute_start >= 0:
                dependencies += attributes[attribute_start:]
            types[name] = (path, set(re.findall(r"\b\w+\b", dependencies)))
            previous_end = end
    references = set(ALLOW)
    for folder in CLIENTS:
        for path in sources(folder):
            if path.relative_to(ROOT).as_posix() in REFERENCE_EXCLUSIONS:
                continue
            references.update(re.findall(r"\b\w+\b", code(path.read_text(encoding="utf-8-sig"))))
    kept = references.intersection(types)
    pending = list(kept - {"MessageBusJsonContext"})
    while pending:
        name = pending.pop()
        for dependency in types[name][1].intersection(types).difference(kept):
            kept.add(dependency)
            pending.append(dependency)
    return [(name, types[name][0]) for name in sorted(types.keys() - kept)]


def main():
    unused = unused_types()
    for name, path in unused:
        print(f"{path.relative_to(ROOT).as_posix()}: {name}")
    return int(bool(unused))


if __name__ == "__main__":
    sys.exit(main())
