#!/usr/bin/env python3
"""Writes src/Passenger.Terminal/WideCharacters.cs from the Unicode Character Database.

A character is two columns wide when its East_Asian_Width is Wide or Fullwidth, or when it is presented as an emoji
(Emoji_Presentation). The files are fetched once into artifacts/unicode/<version>/. VERSION is the Unicode version
.NET's CharUnicodeInfo knows, which the terminal's zero-width rule reads, so the two stay one version.
THIRD_PARTY_NOTICES.md carries the Unicode licence.
"""
import os
import urllib.request

VERSION = "16.0.0"
FILES = {
    "EastAsianWidth.txt": f"https://www.unicode.org/Public/{VERSION}/ucd/EastAsianWidth.txt",
    "emoji-data.txt": f"https://www.unicode.org/Public/{VERSION}/ucd/emoji/emoji-data.txt",
}

# EastAsianWidth.txt's header: unassigned code points in these default to Wide.
WIDE_DEFAULTS = [(0x3400, 0x4DBF), (0x4E00, 0x9FFF), (0xF900, 0xFAFF), (0x20000, 0x2FFFD), (0x30000, 0x3FFFD)]


def fetch(name, cache):
    path = os.path.join(cache, name)
    if not os.path.exists(path):
        with urllib.request.urlopen(FILES[name]) as response:
            data = response.read()
        with open(path, "wb") as out:
            out.write(data)
    with open(path, encoding="utf-8") as text:
        return text.read()


def code_points(text, values):
    found = set()
    for line in text.splitlines():
        line = line.split("#", 1)[0].strip()
        if not line:
            continue
        points, value = (field.strip() for field in line.split(";")[:2])
        if value not in values:
            continue
        first, _, last = points.partition("..")
        found.update(range(int(first, 16), int(last or first, 16) + 1))
    return found


def ranges(points):
    merged = []
    for point in sorted(points):
        if merged and merged[-1][1] == point - 1:
            merged[-1][1] = point
        else:
            merged.append([point, point])
    return merged


def main():
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    cache = os.path.join(root, "artifacts", "unicode", VERSION)
    os.makedirs(cache, exist_ok=True)
    wide = code_points(fetch("EastAsianWidth.txt", cache), {"W", "F"})
    for first, last in WIDE_DEFAULTS:
        wide.update(range(first, last + 1))
    wide |= code_points(fetch("emoji-data.txt", cache), {"Emoji_Presentation"})
    merged = ranges(wide)
    numbers = [f"0x{point:X}" for pair in merged for point in pair]
    rows = [", ".join(numbers[i:i + 12]) + "," for i in range(0, len(numbers), 12)]
    lines = [
        f"// Written by tools/unicode-widths.py from the Unicode Character Database {VERSION} (EastAsianWidth.txt and",
        "// emoji-data.txt, under the Unicode License v3; see THIRD_PARTY_NOTICES.md). Run it again rather than edit this.",
        "namespace Passenger.Terminal;",
        "",
        "/// <summary>The characters a terminal draws two columns wide: East Asian Wide and Fullwidth, and those presented as",
        "/// emoji (the regional indicators besides).</summary>",
        "internal static class WideCharacters",
        "{",
        "    /// <summary>The first and last code point of each range, in order.</summary>",
        "    private static ReadOnlySpan<int> Ranges =>",
        "    [",
        *[f"        {row}" for row in rows],
        "    ];",
        "",
        "    public static bool Contains(int rune)",
        "    {",
        "        ReadOnlySpan<int> ranges = Ranges;",
        "        if (rune < ranges[0] || rune > ranges[^1])",
        "        {",
        "            return false;",
        "        }",
        "        int low = 0;",
        "        int high = (ranges.Length / 2) - 1;",
        "        while (low <= high)",
        "        {",
        "            int middle = (low + high) / 2;",
        "            if (rune < ranges[middle * 2])",
        "            {",
        "                high = middle - 1;",
        "            }",
        "            else if (rune > ranges[(middle * 2) + 1])",
        "            {",
        "                low = middle + 1;",
        "            }",
        "            else",
        "            {",
        "                return true;",
        "            }",
        "        }",
        "        return false;",
        "    }",
        "}",
    ]
    target = os.path.join(root, "src", "Passenger.Terminal", "WideCharacters.cs")
    with open(target, "w", encoding="utf-8", newline="\n") as out:
        out.write("\n".join(lines) + "\n")
    print(f"unicode-widths: {len(merged)} ranges written to {os.path.relpath(target, root)}")


if __name__ == "__main__":
    main()
