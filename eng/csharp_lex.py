"""Tiny C# lexer for eng/verify.py: splits source into code (including interpolation holes) and string contents.

Handles // and /* */ comments, char literals, regular/verbatim strings, interpolated strings ($"", $@"", @$"") with
{hole} code (and {{ }} escapes), and raw strings (\"\"\"...\"\"\", $$\"\"\"...{{hole}}...\"\"\"). Not a full parser:
it only needs to keep prose out of the engine check and keep code in it.
"""
import re

_PREFIX = re.compile(r'(\$*)(@?)(\$*)("+)')
_CHAR = re.compile(r"'(?:[^'\\\n]|\\.)+'")


def split(text):
    """Return (code, strings): code with comments/literals blanked, and the literal contents."""
    code, strings = [], []
    position = _code(text, 0, code, strings, None)
    assert position >= len(text)
    return "".join(code), strings


def _code(text, i, code, strings, closing):
    """Lex code from i; with closing="}" stop at the brace that ends an interpolation hole. Return the index after."""
    depth, n = 0, len(text)
    while i < n:
        c = text[i]
        if text.startswith("//", i):
            end = text.find("\n", i)
            i = n if end == -1 else end
            code.append(" ")
        elif text.startswith("/*", i):
            end = text.find("*/", i + 2)
            i = n if end == -1 else end + 2
            code.append(" ")
        elif c == "'":
            match = _CHAR.match(text, i)
            if match:
                strings.append(match.group()[1:-1])
                code.append(" ")
                i = match.end()
            else:
                code.append(c)
                i += 1
        elif c in '$@"' and _PREFIX.match(text, i) and (i == 0 or not (text[i - 1].isalnum() or text[i - 1] == "_")):
            i = _string(text, i, code, strings)
        elif closing and c == "{":
            depth += 1
            code.append(c)
            i += 1
        elif closing and c == "}":
            if depth == 0:
                return i + 1
            depth -= 1
            code.append(c)
            i += 1
        else:
            code.append(c)
            i += 1
    return i


def _string(text, i, code, strings):
    match = _PREFIX.match(text, i)
    dollars = len(match.group(1)) + len(match.group(3))
    verbatim, quotes = bool(match.group(2)), len(match.group(4))
    i = match.end()
    code.append(" ")
    part = []
    if quotes >= 3:  # raw string literal: ends at the same run of quotes; holes need `dollars` braces
        fence = '"' * quotes
        while i < len(text) and not text.startswith(fence, i):
            if dollars and text.startswith("{" * dollars, i) and not text.startswith("{" * (dollars + 1), i):
                i = _code(text, i + dollars, code, strings, "}")
                i += dollars - 1  # the remaining closing braces
                continue
            part.append(text[i])
            i += 1
        strings.append("".join(part))
        return i + quotes
    if quotes == 2 and not verbatim:  # "" is an empty regular string
        strings.append("")
        return i
    if quotes == 2:  # @"" : empty verbatim string or start with an escaped quote
        i -= 1
    while i < len(text):
        c = text[i]
        if verbatim and text.startswith('""', i):
            part.append('"')
            i += 2
        elif c == '"':
            i += 1
            break
        elif not verbatim and c == "\\" and i + 1 < len(text):
            part.append(text[i:i + 2])
            i += 2
        elif not verbatim and c == "\n":
            break
        elif dollars and c == "{":
            if text.startswith("{{", i):
                part.append("{")
                i += 2
            else:
                i = _code(text, i + 1, code, strings, "}")
        elif dollars and text.startswith("}}", i):
            part.append("}")
            i += 2
        else:
            part.append(c)
            i += 1
    strings.append("".join(part))
    return i


def decode_escapes(code):
    r"""Identifiers may spell letters as \uXXXX / \UXXXXXXXX escapes (\u0047odot is Godot)."""
    return re.sub(r"\\u([0-9a-fA-F]{4})|\\U([0-9a-fA-F]{8})", lambda m: chr(int(m.group(1) or m.group(2), 16)), code)


ENGINE_IDENTIFIER = re.compile(r"\bGodot\b")
ENGINE_TYPE_NAME = re.compile(r"(?<![\w.])(?:global::)?Godot\.[A-Za-z_]")  # "Godot.Node" for Type.GetType etc.


def engine_references(text):
    """Engine uses in a C# file: identifiers in code (holes included) and Godot type names in string literals."""
    code, strings = split(text)
    found = [m.group() for m in ENGINE_IDENTIFIER.finditer(decode_escapes(code))]
    found += [s for s in strings if ENGINE_TYPE_NAME.search(s)]
    return found
