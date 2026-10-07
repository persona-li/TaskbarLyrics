// Mirrors UnicodeScriptDetector + KaraokeTextLayout.ApplyScriptFonts.
export type FontRun = { text: string; family: string };
export function resolvePreviewLine<
  T extends { start: number; duration: number },
>(lines: readonly T[], position: number): T | undefined {
  let lo = 0,
    hi = lines.length;
  const p = Math.max(0, position);
  while (lo < hi) {
    const middle = (lo + hi) >>> 1;
    if (lines[middle].start <= p) lo = middle + 1;
    else hi = middle;
  }
  const index = lo - 1;
  if (index < 0) return undefined;
  const line = lines[index],
    end = line.start + line.duration;
  if (position <= end || line.duration <= 0) return line;
  const shortGap =
    index + 1 < lines.length && lines[index + 1].start - end <= 2000;
  return shortGap || position - end <= 1500 ? line : undefined;
}
function script(character: string): string {
  const cp = character.codePointAt(0)!;
  if (/^[\s\p{P}\p{M}]$/u.test(character)) return "";
  if ((cp >= 0x3040 && cp <= 0x30ff) || (cp >= 0xff65 && cp <= 0xff9f))
    return "japanese";
  if (
    (cp >= 0xac00 && cp <= 0xd7af) ||
    (cp >= 0x1100 && cp <= 0x11ff) ||
    (cp >= 0x3130 && cp <= 0x318f) ||
    (cp >= 0xa960 && cp <= 0xa97f) ||
    (cp >= 0xd7b0 && cp <= 0xd7ff)
  )
    return "korean";
  if (
    (cp >= 0x4e00 && cp <= 0x9fff) ||
    (cp >= 0x3400 && cp <= 0x4dbf) ||
    (cp >= 0xf900 && cp <= 0xfaff) ||
    (cp >= 0x20000 && cp <= 0x2ceaf)
  )
    return "chinese";
  if (
    (cp >= 0x400 && cp <= 0x52f) ||
    (cp >= 0x2de0 && cp <= 0x2dff) ||
    (cp >= 0xa640 && cp <= 0xa69f)
  )
    return "cyrillic";
  if (
    (cp >= 0x600 && cp <= 0x6ff) ||
    (cp >= 0x750 && cp <= 0x77f) ||
    (cp >= 0x8a0 && cp <= 0x8ff) ||
    (cp >= 0xfb50 && cp <= 0xfdff) ||
    (cp >= 0xfe70 && cp <= 0xfeff)
  )
    return "arabic";
  if (
    (cp >= 0x30 && cp <= 0x39) ||
    (cp >= 0x41 && cp <= 0x5a) ||
    (cp >= 0x61 && cp <= 0x7a) ||
    (cp >= 0xc0 && cp <= 0x24f) ||
    (cp >= 0x1e00 && cp <= 0x1eff) ||
    (cp >= 0xff21 && cp <= 0xff3a) ||
    (cp >= 0xff41 && cp <= 0xff5a)
  )
    return "latin";
  return "";
}
export function fontRuns(
  text: string,
  fonts: Record<string, string>,
  fullLine = text,
): FontRun[] {
  const letters = Array.from(text),
    hasKana = /[\u3040-\u30ff\uff65-\uff9f]/u.test(fullLine);
  const scripts = letters.map((c) => {
    const s = script(c);
    return s === "chinese" && hasKana ? "japanese" : s;
  });
  for (let i = 0; i < scripts.length; i++)
    if (!scripts[i]) {
      let previous = "",
        next = "";
      for (let p = i - 1; p >= 0; p--)
        if (scripts[p]) {
          previous = scripts[p];
          break;
        }
      for (let n = i + 1; n < scripts.length; n++)
        if (scripts[n]) {
          next = scripts[n];
          break;
        }
      scripts[i] = previous || next || "latin";
    }
  const result: FontRun[] = [];
  for (let i = 0; i < letters.length; i++) {
    const family = fonts[scripts[i]] || fonts.other || "Segoe UI";
    if (result.length && result[result.length - 1].family === family)
      result[result.length - 1].text += letters[i];
    else result.push({ text: letters[i], family });
  }
  return result;
}
