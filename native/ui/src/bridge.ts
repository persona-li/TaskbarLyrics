export type Line = {
  text: string;
  translation: string;
  start: number;
  duration: number;
  words: { text: string; start: number; duration: number }[];
};
export type Settings = {
  font: string;
  fontSize: number;
  normal: string;
  highlight: string;
  hue: number;
  shadow: boolean;
  overlay: boolean;
  theme: string;
  globalOffset: number;
  trackOffsets: Record<string, number>;
  alignment: "Left" | "Center" | "Right";
  karaoke: boolean;
  autoPan: boolean;
  fonts: Record<string, string>;
  shadowColor: string;
  shadowOffsetX: number;
  shadowOffsetY: number;
  autoLayout: boolean;
  verticalOffset: number;
  leftMargin: number;
  rightMargin: number;
  minWidth: number;
  maxWidth: number;
  fallbackWidthPercent: number;
  startup: boolean;
  animations: boolean;
  recentColors: Record<"normal" | "highlight" | "shadow", string[]>;
};
export type State = {
  maximized?: boolean;
  title: string;
  artist: string;
  album: string;
  source: string;
  key: string;
  position: number;
  duration: number;
  playing: boolean;
  toggle: boolean;
  previous: boolean;
  next: boolean;
  seek: boolean;
  captured: number;
  lines: Line[];
  settings: Settings;
  origin: string;
  error: string;
  loading: boolean;
  instrumental: boolean;
  rawPlaying: boolean;
  rawStatus: string;
  playbackRate: number;
  offsetKey: string;
  systemTheme: "light" | "dark";
  layoutMeasured: boolean;
  cacheCount: number;
  cacheSize: string;
  manualCount: number;
  offsetCount: number;
  version: string;
  saveError: string;
};
export type Candidate = {
  id: string;
  mid: string;
  title: string;
  artist: string;
  album: string;
  duration: number;
};
type WebView = {
  postMessage: (message: unknown) => void;
  addEventListener: (
    event: "message",
    listener: (e: { data: unknown }) => void,
  ) => void;
  removeEventListener: (
    event: "message",
    listener: (e: { data: unknown }) => void,
  ) => void;
};
declare global {
  interface Window {
    chrome?: { webview?: WebView };
  }
}
export const webview = window.chrome?.webview;
export const send = (type: string, payload: Record<string, unknown> = {}) =>
  webview?.postMessage({ type, ...payload });
export const defaults: Settings = {
  font: "Microsoft YaHei UI",
  fontSize: 14,
  normal: "#FF496DBF",
  highlight: "#FFA0CCEE",
  hue: 264.540965113,
  shadow: true,
  overlay: true,
  theme: "system",
  globalOffset: 0,
  trackOffsets: {},
  alignment: "Center", karaoke: true, autoPan: true,
  fonts: {chinese:"Microsoft YaHei UI",japanese:"Microsoft YaHei UI",korean:"Microsoft YaHei UI",latin:"Microsoft YaHei UI",cyrillic:"Microsoft YaHei UI",arabic:"Microsoft YaHei UI",other:"Microsoft YaHei UI"},
  shadowColor: "#80000000", shadowOffsetX: 1, shadowOffsetY: 1,
  autoLayout: true, verticalOffset: 0, leftMargin: 8, rightMargin: 40,
  minWidth: 750, maxWidth: 1350, fallbackWidthPercent: 60,
  startup: false, animations: true,
  recentColors: {normal:[], highlight:[], shadow:[]},
};
export const empty: State = {
  title: "连接播放器",
  artist: "",
  album: "",
  source: "",
  key: "",
  position: 0,
  duration: 0,
  playing: false,
  toggle: false,
  previous: false,
  next: false,
  seek: false,
  captured: 0,
  lines: [],
  settings: defaults,
  origin: "",
  error: "",
  loading: false,
  instrumental: false,
  rawPlaying: false, rawStatus: "Closed", playbackRate: 1, offsetKey: "", systemTheme: "light",
  layoutMeasured: false,cacheCount: 0,cacheSize: "0 MB",manualCount: 0,offsetCount: 0,
  version: "1.0.0",saveError: "",
};
export const cssColor = (argb: string) =>
  argb.length === 9 ? "#" + argb.slice(3) + argb.slice(1, 3) : argb;
export function findLine(lines: Line[], p: number) {
  let lo = 0,
    hi = lines.length;
  while (lo < hi) {
    const m = (lo + hi) >>> 1;
    if (lines[m].start <= p) lo = m + 1;
    else hi = m;
  }
  return lo - 1;
}
export function stamp(p: number) {
  const n = Math.max(0, Math.floor(p / 1000));
  return `${Math.floor(n / 60)
    .toString()
    .padStart(2, "0")}:${(n % 60).toString().padStart(2, "0")}`;
}

