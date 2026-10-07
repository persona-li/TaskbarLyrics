import test from "node:test";
import assert from "node:assert/strict";
import { fontRuns, resolvePreviewLine } from "../src/typography.ts";
const fonts = {
  chinese: "Chinese",
  japanese: "Japanese",
  korean: "Korean",
  latin: "Latin",
  cyrillic: "Cyrillic",
  arabic: "Arabic",
  other: "Other",
};
test("script fonts preserve contiguous Arabic shaping and Japanese Kanji context", () => {
  for (const [text, font] of [
    ["中文", "Chinese"],
    ["漢字かな", "Japanese"],
    ["안녕하세요", "Korean"],
    ["Привет", "Cyrillic"],
    ["مرحبا بالعالم", "Arabic"],
  ])
    assert.deepEqual(fontRuns(text, fonts), [{ text, family: font }]);
  assert.deepEqual(fontRuns("漢字", fonts, "漢字かな"), [
    { text: "漢字", family: "Japanese" },
  ]);
  assert.deepEqual(fontRuns("中文 ABC", fonts), [
    { text: "中文 ", family: "Chinese" },
    { text: "ABC", family: "Latin" },
  ]);
  assert.deepEqual(fontRuns("…مرحبا", fonts), [
    { text: "…مرحبا", family: "Arabic" },
  ]);
});
test("preview keeps short gaps, releases long gaps after hold and respects exact boundaries", () => {
  const a = { start: 1000, duration: 1000 },
    b = { start: 4000, duration: 1000 },
    c = { start: 9000, duration: 1000 };
  const lines = [a, b, c];
  assert.equal(resolvePreviewLine(lines, 999), undefined);
  assert.equal(resolvePreviewLine(lines, 2000), a);
  assert.equal(resolvePreviewLine(lines, 3999), a);
  assert.equal(resolvePreviewLine(lines, 4000), b);
  assert.equal(resolvePreviewLine(lines, 6500), b);
  assert.equal(resolvePreviewLine(lines, 6501), undefined);
  assert.equal(resolvePreviewLine(lines, 11500), c);
  assert.equal(resolvePreviewLine(lines, 11501), undefined);
  const open = { start: 0, duration: 0 };
  assert.equal(resolvePreviewLine([open], 100000), open);
});
