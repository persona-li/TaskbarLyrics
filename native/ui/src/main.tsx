import React, { useEffect, useLayoutEffect, useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import {
  Candidate,
  cssColor,
  empty,
  findLine,
  Line,
  send,
  Settings,
  stamp,
  State,
  webview,
} from "./bridge";
import {
  Icon,
  Logo,
  Toggle,
  Row,
  Segments,
  Presence,
  SlidingContent,
  Disclosure,
  NumberField,
  Choice,
  FontPicker,
  ColorEditor,
} from "./controls";
import "./style.css";
import { fontRuns, resolvePreviewLine } from "./typography";
const pageNames = ["正在播放", "外观", "时间校准", "设置", "关于"];
const presets = [
  ["原蓝", 264.540965113],
  ["紫藤", 305],
  ["玫瑰", 345],
  ["珊瑚", 25],
  ["焦糖", 65],
  ["橄榄", 105],
  ["松绿", 185],
  ["湖青", 225],
] as const;
function Transport({ s }: { s: State }) {
  return (
    <div className="transport">
      {["previous", s.playing ? "pause" : "play", "next"].map((name, i) => (
        <button
          className="icon-button"
          key={i}
          aria-label={["上一首", s.playing ? "暂停" : "播放", "下一首"][i]}
          disabled={![s.previous, s.toggle, s.next][i]}
          onClick={() => send(["previous", "toggle", "next"][i])}
        >
          <Icon name={name} />
        </button>
      ))}
    </div>
  );
}
function Karaoke({
  line,
  position,
  enabled = true,
  settings,
}: {
  line: Line;
  position: number;
  enabled?: boolean;
  settings: Settings;
}) {
  const ribbon = useRef<HTMLSpanElement>(null),
    pan = useRef(0),
    lastLine = useRef("");
  const runs = (text: string) =>
    fontRuns(
      text,
      { ...settings.fonts, chinese: settings.font },
      line.text,
    ).map((run, i) => (
      <span key={i} style={{ fontFamily: `"${run.family}"` }}>
        {run.text}
      </span>
    ));
  useLayoutEffect(() => {
    const text = ribbon.current,
      viewport = text?.parentElement;
    if (!text || !viewport) return;
    const key = line.start + ":" + line.text;
    if (lastLine.current !== key) {
      lastLine.current = key;
      pan.current = 0;
    }
    const width = viewport.clientWidth,
      total = text.offsetWidth;
    const overflow = total > width + 0.5;
    viewport.style.textAlign = overflow
      ? "left"
      : (settings.alignment || "Center").toLowerCase();
    let target = pan.current;
    if (!overflow || !settings.autoPan) target = 0;
    else if (!line.words.length) {
      const progress = Math.max(
        0,
        Math.min(1, (position - line.start) / Math.max(1, line.duration)),
      );
      target = Math.max(
        0,
        Math.min(total - width, total * progress - width * 0.7),
      );
    } else {
      let index = -1;
      for (let i = 0; i < line.words.length; i++)
        if (position >= line.words[i].start) index = i;
      const word =
        index < 0
          ? null
          : text.querySelector<HTMLElement>(`[data-word="${index}"]`);
      if (!word) target = 0;
      else {
        const timing = line.words[index],
          progress = Math.max(
            0,
            Math.min(
              1,
              (position - timing.start) / Math.max(1, timing.duration),
            ),
          );
        const left = word.offsetLeft,
          right = left + word.offsetWidth,
          cursor = left + word.offsetWidth * progress;
        if (!(left >= pan.current + 8 && right <= pan.current + width - 20))
          target = Math.max(0, Math.min(total - width, cursor - width * 0.7));
      }
    }
    pan.current =
      Math.abs(target - pan.current) < 0.5
        ? target
        : pan.current + (target - pan.current) * 0.35;
    text.style.transform = `translateX(${-pan.current}px)`;
  }, [line.text, line.start, position, settings]);
  return (
    <span ref={ribbon} className="karaoke-ribbon">
      {line.words.length ? (
        line.words.map((w, i) => {
          const progress =
            (enabled
              ? Math.max(
                  0,
                  Math.min(1, (position - w.start) / Math.max(1, w.duration)),
                )
              : 0) * 100;
          return (
            <span
              key={i}
              data-word={i}
              style={{
                backgroundImage: `linear-gradient(to right,var(--highlight) ${progress}%,var(--normal) ${progress}%)`,
                backgroundClip: "text",
                color: "transparent",
              }}
            >
              {runs(w.text)}
            </span>
          );
        })
      ) : (
        <span
          style={{
            color: "transparent",
            backgroundImage: `linear-gradient(to right,var(--highlight) ${enabled ? Math.max(0, Math.min(1, (position - line.start) / Math.max(1, line.duration))) * 100 : 0}%,var(--normal) 0)`,
            backgroundClip: "text",
          }}
        >
          {runs(line.text)}
        </span>
      )}
    </span>
  );
}
function Lyrics({ s, position }: { s: State; position: number }) {
  const active = findLine(s.lines, position),
    scroll = useRef<HTMLDivElement>(null),
    row = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const r = row.current,
      c = scroll.current;
    if (r && c)
      c.scrollTo({
        top:
          r.offsetTop - c.offsetTop - c.clientHeight / 2 + r.clientHeight / 2,
        behavior: "smooth",
      });
  }, [active, s.key]);
  useEffect(() => {
    const c = scroll.current;
    if (!c) return;
    const ob = new ResizeObserver(() => {
      c.style.setProperty("--lyric-padding", `${c.clientHeight / 2}px`);
      const r = row.current;
      if (r)
        c.scrollTop =
          r.offsetTop - c.offsetTop - c.clientHeight / 2 + r.clientHeight / 2;
    });
    ob.observe(c);
    const follow = (e: WheelEvent) => e.preventDefault();
    c.addEventListener("wheel", follow, { passive: false });
    return () => {
      ob.disconnect();
      c.removeEventListener("wheel", follow);
    };
  }, []);
  return (
    <div className="lyrics-mask">
      <div ref={scroll} className="lyrics-scroll">
        {s.lines.length ? (
          s.lines.map((l, i) => (
            <div
              key={i}
              ref={i === active ? row : null}
              className={
                "lyric-row " +
                (i === active ? "active" : i < active ? "past" : "future")
              }
            >
              <div className="reader-lyric">{l.text}</div>
              {l.translation && (
                <div className="translation">{l.translation}</div>
              )}
            </div>
          ))
        ) : (
          <div className="empty-lyric">
            {(s as State & { instrumental?: boolean }).instrumental
              ? "♪ 纯音乐"
              : s.key
                ? s.title
                : "播放一首歌曲"}
          </div>
        )}
      </div>
    </div>
  );
}
function Progress({
  s,
  position,
  onPosition,
}: {
  s: State;
  position: number;
  onPosition: (p: number) => void;
}) {
  const [drag, setDrag] = useState<number | null>(null);
  useEffect(() => setDrag(null), [s.key]);
  const v = drag ?? position;
  return (
    <input
      className="progress"
      aria-label="播放进度"
      type="range"
      min={0}
      max={s.duration || 1}
      step={1}
      value={v}
      disabled={!s.seek}
      style={
        {
          "--progress": `${Math.max(0, Math.min(100, (v / Math.max(1, s.duration)) * 100))}%`,
        } as React.CSSProperties
      }
      onChange={(e) => {
        const p = Number(e.target.value);
        setDrag(p);
        onPosition(p);
      }}
      onPointerUp={(e) => {
        const p = Number(e.currentTarget.value);
        send("seek", { position: p });
        onPosition(p);
        setDrag(null);
      }}
      onKeyUp={(e) => {
        if (["ArrowLeft", "ArrowRight", "Home", "End"].includes(e.key)) {
          send("seek", { position: Number(e.currentTarget.value) });
          setDrag(null);
        }
      }}
    />
  );
}
function App() {
  const [s, setState] = useState<State>(empty),
    [page, setPage] = useState(0),
    [previousPage, setPreviousPage] = useState(0),
    [tab, setTab] = useState(0),
    [previousTab, setPreviousTab] = useState(0),
    [darkPreview, setDarkPreview] = useState(1),
    [previewSource, setPreviewSource] = useState("示例"),
    [previewPlaying, setPreviewPlaying] = useState(true),
    [examplePosition, setExamplePosition] = useState(0),
    [languages, setLanguages] = useState(false),
    [custom, setCustom] = useState(false),
    [manual, setManual] = useState(false),
    [maintenance, setMaintenance] = useState(false),
    [searchOpen, setSearchOpen] = useState(false),
    [searchContext, setSearchContext] = useState({
      key: "",
      title: "",
      artist: "",
      duration: 0,
    }),
    [searchPage, setSearchPage] = useState(1),
    [hasMore, setHasMore] = useState(false),
    [searched, setSearched] = useState(false),
    [query, setQuery] = useState(""),
    [queryEditing, setQueryEditing] = useState(false),
    [results, setResults] = useState<Candidate[]>([]),
    [selected, setSelected] = useState<Candidate>(),
    [searching, setSearching] = useState(false),
    [applying, setApplying] = useState(false),
    [searchError, setSearchError] = useState(""),
    [step, setStep] = useState(100),
    [position, setPosition] = useState(0),
    [fonts, setFonts] = useState<string[]>([]),
    [schemes, setSchemes] = useState<{ normal: string; highlight: string }[]>(
      [],
    ),
    [band, setBand] = useState<string[]>([]),
    [confirm, setConfirm] = useState<{
      label: string;
      operation: string;
    } | null>(null);
  const received = useRef({
    position: 0,
    time: performance.now(),
    playing: false,
    rate: 1,
  });
  const currentState = useRef(s);
  currentState.current = s;
  useEffect(() => {
    const listener = (e: { data: unknown }) => {
      const m = e.data as {
        type: string;
        state: State;
        results: Candidate[];
        fonts: string[];
        palettes: { normal: string; highlight: string }[];
        band: string[];
        append?: boolean;
        hasMore?: boolean;
        ok?: boolean;
        error?: string;
      };
      if (m.type === "state") {
        setState((old) => ({
          ...m.state,
          lines: m.state.lines ?? (m.state.key === old.key ? old.lines : []),
        }));
        received.current = {
          position: m.state.position,
          time: performance.now(),
          playing: m.state.rawPlaying ?? m.state.playing,
          rate: m.state.playbackRate ?? 1,
        };
        setPosition(m.state.position);
      }
      if (m.type === "search") {
        setResults((old) =>
          m.append
            ? [
                ...old,
                ...(m.results || []).filter(
                  (c) => !old.some((o) => o.mid === c.mid),
                ),
              ]
            : m.results || [],
        );
        setHasMore(m.hasMore ?? (m.results || []).length >= 20);
        setSearched(true);
        setSearching(false);
        setSearchError(m.error || "");
      }
      if (m.type === "applied") {
        setApplying(false);
        if (m.ok) setSearchOpen(false);
        else setSearchError(m.error || "歌词应用失败，请选择其他版本。");
      }
      if (m.type === "fonts") setFonts(m.fonts);
      if (m.type === "openSearch") {
        setSearchOpen(true);
        setQueryEditing(false);
        const c = currentState.current;
        setSearchContext({
          key: c.key,
          title: c.title,
          artist: c.artist,
          duration: c.duration,
        });
        setQuery(`${c.title} ${c.artist}`.trim());
        setResults([]);
        setSelected(undefined);
        setSearched(false);
        const q = `${c.title} ${c.artist}`.trim();
        setSearchPage(1);
        setSearching(!!c.key);
        if (c.key) send("search", { query: q, page: 1, append: false });
      }
      if (m.type === "palettes") {
        setSchemes(m.palettes);
        setBand(m.band);
      }
    };
    webview?.addEventListener("message", listener);
    send("ready");
    return () => webview?.removeEventListener("message", listener);
  }, []);
  useEffect(() => {
    const timer = setInterval(() => {
      const r = received.current;
      setPosition(
        Math.min(
          s.duration || Infinity,
          r.position + (r.playing ? (performance.now() - r.time) * r.rate : 0),
        ),
      );
    }, 40);
    return () => clearInterval(timer);
  }, [s.duration]);
  useEffect(() => {
    if (!previewPlaying) return;
    const timer = setInterval(
      () => setExamplePosition((p) => (p + 40) % 6000),
      40,
    );
    return () => clearInterval(timer);
  }, [previewPlaying]);
  useEffect(() => {
    const key = (e: KeyboardEvent) => {
      const element = e.target as HTMLElement;
      const editing =
        element instanceof HTMLInputElement ||
        element instanceof HTMLTextAreaElement ||
        element.isContentEditable;
      if (
        !editing &&
        (e.ctrlKey || e.altKey || e.metaKey || e.key === "F1" || e.key === "F5")
      )
        e.preventDefault();
    };
    window.addEventListener("keydown", key);
    return () => window.removeEventListener("keydown", key);
  }, []);
  const settings = s.settings as Settings;
  const patch = (values: Partial<Settings>) => {
    setState((old) => ({ ...old, settings: { ...old.settings, ...values } }));
    send("settings", { values });
  };
  const offsetKey = s.offsetKey || s.key;
  const offset =
      settings.globalOffset + (settings.trackOffsets[offsetKey] || 0),
    effective = position + offset;
  const theme =
    settings.theme === "system" ? s.systemTheme || "light" : settings.theme;
  const setPositionNow = (p: number) => {
    received.current = {
      position: p,
      time: performance.now(),
      playing: s.rawPlaying ?? s.playing,
      rate: s.playbackRate ?? 1,
    };
    setPosition(p);
  };
  const style = {
    "--normal": cssColor(settings.normal),
    "--highlight": cssColor(settings.highlight),
    "--font": settings.font,
  } as React.CSSProperties;
  const customOpen =
      custom ||
      settings.hue < 0 ||
      !presets.some(([, h]) => Math.abs(settings.hue - h) < 0.001),
    manualOpen = manual || settings.hue < 0;
  const remember = (
    role: "normal" | "highlight" | "shadow",
    color: string,
    previous?: string,
  ) => {
    const recentColors = {
      ...settings.recentColors,
      [role]: [
        color,
        ...(previous && previous !== color ? [previous] : []),
        ...(settings.recentColors?.[role] || []).filter(
          (c) => c !== color && c !== previous,
        ),
      ].slice(0, 6),
    };
    patch({ recentColors });
  };
  const color = (role: "normal" | "highlight" | "shadow", label: string) => {
    const field = role === "shadow" ? "shadowColor" : role;
    return (
      <ColorEditor
        label={label}
        value={settings[field] || "#80000000"}
        recent={settings.recentColors?.[role] || []}
        onChange={(v) => {
          patch({ [field]: v, ...(role !== "shadow" ? { hue: -1 } : {}) });
          if (role !== "shadow") {
            setCustom(true);
            setManual(true);
          }
        }}
        onRemember={(v, previous) => remember(role, v, previous)}
      />
    );
  };
  const settingSlider = (
    label: string,
    field: keyof Settings,
    fallback: number,
    min: number,
    max: number,
    unit = "px",
    step = 1,
  ) => (
    <Row label={label}>
      <div className="slider-editor">
        <input
          type="range"
          aria-label={label + "滑块"}
          min={min}
          max={max}
          step={step}
          value={Number(settings[field] ?? fallback)}
          style={{
            background: `linear-gradient(to right,var(--accent) ${Math.max(0, Math.min(100, ((Number(settings[field] ?? fallback) - min) / (max - min)) * 100))}%,var(--line) 0)`,
          }}
          onChange={(e) => patch({ [field]: Number(e.target.value) })}
        />
        <NumberField
          label={label}
          value={Number(settings[field] ?? fallback)}
          min={min}
          max={max}
          step={step}
          unit={unit}
          onChange={(v) => patch({ [field]: v })}
        />
      </div>
    </Row>
  );
  const previewLine = resolvePreviewLine(s.lines, effective);
  const example: Line = {
    text: "此刻，风经过窗前",
    translation: "",
    start: 0,
    duration: 5000,
    words: Array.from("此刻，风经过窗前").map((text, i) => ({
      text,
      start: i * 500,
      duration: 500,
    })),
  };
  const Preview = ({ currentOnly = false }: { currentOnly?: boolean }) => (
    <div className={"preview-widget " + (currentOnly ? "current-only" : "")}>
      {!currentOnly && (
        <div className="preview-head">
          <span>预览</span>
          <div className="preview-controls">
            <Choice
              value={previewSource}
              items={["示例", "当前歌曲"]}
              onChange={setPreviewSource}
              label="预览来源"
              className="source-choice"
            />
            <Segments
              items={["浅色", "深色"]}
              value={darkPreview}
              onChange={setDarkPreview}
            />
            <button
              className="icon-button"
              aria-label={previewPlaying ? "暂停预览" : "播放预览"}
              onClick={() => setPreviewPlaying(!previewPlaying)}
            >
              <Icon name={previewPlaying ? "pause" : "play"} />
            </button>
          </div>
        </div>
      )}
      <div className={"preview " + (darkPreview ? "dark-preview" : "")}>
        <div
          className="preview-lyrics"
          style={{
            fontFamily: [settings.font, ...Object.values(settings.fonts || {})]
              .map((font) => `"${font}"`)
              .join(","),
            fontSize: settings.fontSize * 1.15,
            fontWeight: 600,
            textAlign: (settings.alignment || "Center").toLowerCase() as
              "left" | "center" | "right",
            textShadow: settings.shadow
              ? `${settings.shadowOffsetX ?? 1}px ${settings.shadowOffsetY ?? 1}px 0 ${cssColor(settings.shadowColor || "#80000000")}`
              : "none",
          }}
        >
          {currentOnly || previewSource === "当前歌曲" ? (
            previewLine ? (
              <Karaoke
                line={previewLine}
                position={effective}
                enabled={settings.karaoke !== false}
                settings={settings}
              />
            ) : (
              <span className="preview-hint">
                {s.lines.length
                  ? ""
                  : (s as State & { instrumental?: boolean }).instrumental
                    ? "♪ 纯音乐"
                    : "暂无歌词"}
              </span>
            )
          ) : (
            <Karaoke
              line={example}
              position={examplePosition}
              enabled={settings.karaoke !== false}
              settings={settings}
            />
          )}
        </div>
      </div>
    </div>
  );
  const calibrate = (global: boolean) => {
    const value = global
      ? settings.globalOffset
      : settings.trackOffsets[offsetKey] || 0;
    const change = (v: number) => {
      v = Math.max(-5000, Math.min(5000, Math.round(v)));
      patch(
        global
          ? { globalOffset: v }
          : { trackOffsets: { ...settings.trackOffsets, [offsetKey]: v } },
      );
    };
    return (
      <section className="calibration">
        <div className="section-head">
          <span>{global ? "全局校准" : "当前歌曲"}</span>
          <button
            className="text-button reset-timing"
            disabled={!global && !s.key}
            onClick={() => change(0)}
          >
            {global ? "重置全局" : "重置本曲"}
          </button>
        </div>
        <div className="calibration-tools">
          <div className="offset-group">
            <button
              className="text-button"
              disabled={!global && !s.key}
              onClick={() => change(value - step)}
            >
              延后 <span>{step} ms</span>
            </button>
            <NumberField
              label={global ? "全局偏移" : "本曲偏移"}
              value={value}
              disabled={!global && !s.key}
              min={-5000}
              max={5000}
              step={step}
              unit="ms"
              onChange={change}
            />
            <button
              className="text-button"
              disabled={!global && !s.key}
              onClick={() => change(value + step)}
            >
              提前 <span>{step} ms</span>
            </button>
          </div>
          <Choice
            value={`${step} ms`}
            items={["50 ms", "100 ms", "500 ms"]}
            prefix="每次 "
            onChange={(v) => setStep(parseInt(v))}
            label={global ? "全局校准每次调整量" : "每次调整量"}
            className="timing-choice"
          />
        </div>
      </section>
    );
  };
  const search = (more = false) => {
    setSearching(true);
    setSearchError("");
    if (!more) setSelected(undefined);
    const nextPage = more ? searchPage + 1 : 1;
    setSearchPage(nextPage);
    send("search", { query, page: nextPage, append: more });
  };
  const openSearch = () => {
    setQuery(`${s.title} ${s.artist}`.trim());
    setQueryEditing(false);
    setSearchOpen(true);
    setSelected(undefined);
    setResults([]);
    setApplying(false);
    setSearchError("");
    setSearched(false);
    setHasMore(false);
    setSearchContext({
      key: s.key,
      title: s.title,
      artist: s.artist,
      duration: s.duration,
    });
    setSearchPage(1);
    setSearching(!!s.key);
    if (s.key)
      send("search", {
        query: `${s.title} ${s.artist}`.trim(),
        page: 1,
        append: false,
      });
  };
  return (
    <div
      className={
        "app " +
        theme +
        (settings.animations === false ? " reduced-motion" : "")
      }
      style={style}
    >
      {!s.maximized && ["left", "right", "top", "bottom", "top-left", "top-right", "bottom-left", "bottom-right"].map(edge => (
        <div key={edge} className={"window-resizer " + edge} aria-hidden="true"
          onPointerDown={e => {
            if (e.button !== 0) return;
            e.preventDefault(); e.stopPropagation();
            send("window", { action: "resize", edge });
          }} />
      ))}
      <header
        className="titlebar"
        onPointerDown={(e) => {
          if (e.button === 0 && !(e.target as HTMLElement).closest("button"))
            send("window", { action: "drag" });
        }}
        onDoubleClick={(e) => {
          if (!(e.target as HTMLElement).closest("button"))
            send("window", { action: "maximize" });
        }}
      >
        <div className="brand">
          <Logo />
          <span>TaskbarLyrics</span>
        </div>
        <div className="window-actions">
          {["minimize", "maximize", "close"].map((name, i) => (
            <button
              key={name}
              aria-label={["最小化", "最大化或还原", "收起到托盘"][i]}
              onClick={() => send("window", { action: name })}
            >
              <Icon name={name} />
            </button>
          ))}
        </div>
      </header>
      <aside>
        <nav>
          {pageNames.slice(0, 4).map((name, i) => (
            <button
              className={page === i ? "selected" : ""}
              key={name}
              onClick={() => { setPreviousPage(page); setPage(i); }}
            >
              <Icon name={["music", "palette", "clock", "settings"][i]} />
              <span>{name}</span>
            </button>
          ))}
        </nav>
        <div className="sidebar-bottom">
          <div className="separator" />
          <div className="overlay-switch">
            <Icon name="screen" />
            <span>任务栏歌词</span>
            <Toggle
              label="任务栏歌词"
              value={settings.overlay}
              onChange={() => patch({ overlay: !settings.overlay })}
            />
          </div>
          <button
            className={page === 4 ? "selected" : ""}
            onClick={() => { setPreviousPage(page); setPage(4); }}
          >
            <Icon name="info" />
            <span>关于</span>
          </button>
        </div>
      </aside>
      <main>
        {s.error && (
          <div className="notice" role="status">
            {s.error}
          </div>
        )}
        <SlidingContent value={page} direction={page < previousPage ? -1 : 1} axis="y">
        <div className={"page page-" + page} key={page}>
          <h1>
            {page === 0
              ? s.playing
                ? "正在播放"
                : s.key
                  ? "已暂停"
                  : "正在播放"
              : pageNames[page]}
          </h1>
          {page === 0 && (
            <section className="card playing-card">
              <div className="track">
                <div className="track-details" key={s.key}>
                  <h2>{s.title}</h2>
                  <p>{s.artist}</p>
                  <small>{s.album}</small>
                </div>
                <button
                  className={
                    "text-button choose-lyrics " +
                    (!s.lines.length &&
                    s.key &&
                    !s.loading &&
                    !(s as State & { instrumental?: boolean }).instrumental
                      ? "breathe"
                      : "")
                  }
                  onClick={openSearch}
                >
                  选择歌词
                </button>
              </div>
              <Lyrics s={s} position={effective} />
              <div className="playback">
                <Progress
                  s={s}
                  position={position}
                  onPosition={setPositionNow}
                />
                <div className="playback-bottom">
                  <Transport s={s} />
                  <span>
                    {stamp(position)} / {stamp(s.duration)}
                  </span>
                </div>
              </div>
            </section>
          )}
          {page === 1 && (
            <section
              className={"card appearance " + (tab === 1 ? "color-card" : "")}
            >
              <div className="appearance-preview">{Preview({})}</div>
              <div className="separator" />
              <div className="tabs-right">
                <Segments
                  items={["文字", "颜色", "位置"]}
                  value={tab}
                  onChange={(i) => {
                    setPreviousTab(tab);
                    setTab(i);
                  }}
                />
              </div>
              <div className="editor-scroll">
                <SlidingContent value={tab} direction={tab < previousTab ? -1 : 1}>
                  {tab === 0 && (
                    <>
                      <Row label="字体">
                        <FontPicker
                          label="中文字体"
                          value={settings.font}
                          fonts={fonts}
                          onChange={(v) =>
                            patch({
                              font: v,
                              fonts: { ...settings.fonts, chinese: v },
                            })
                          }
                        />
                      </Row>
                      {settingSlider("字号", "fontSize", 14, 10, 72)}
                      <Row label="对齐">
                        <Segments
                          items={["left", "center", "right"].map((n) => (
                            <Icon name={n} />
                          ))}
                          value={["Left", "Center", "Right"].indexOf(
                            settings.alignment || "Center",
                          )}
                          onChange={(i) =>
                            patch({
                              alignment: (["Left", "Center", "Right"] as const)[
                                i
                              ],
                            })
                          }
                        />
                      </Row>
                      <Row label="逐字高亮">
                        <Toggle
                          label="逐字高亮"
                          value={settings.karaoke !== false}
                          onChange={() =>
                            patch({ karaoke: settings.karaoke === false })
                          }
                        />
                      </Row>
                      <Row label="长句滚动">
                        <Toggle
                          label="长句滚动"
                          value={settings.autoPan !== false}
                          onChange={() =>
                            patch({ autoPan: settings.autoPan === false })
                          }
                        />
                      </Row>
                      <Disclosure
                        label="按语言设置"
                        open={languages}
                        onChange={() => setLanguages(!languages)}
                      >
                        {[
                          ["西文", "latin"],
                          ["日文", "japanese"],
                          ["韩文", "korean"],
                          ["西里尔字母", "cyrillic"],
                          ["阿拉伯文", "arabic"],
                          ["其他", "other"],
                        ].map(([label, key]) => (
                          <Row key={key} label={label}>
                            <FontPicker
                              label={label + "字体"}
                              value={settings.fonts?.[key] || settings.font}
                              fonts={fonts}
                              onChange={(v) =>
                                patch({
                                  fonts: { ...settings.fonts, [key]: v },
                                })
                              }
                            />
                          </Row>
                        ))}
                      </Disclosure>
                    </>
                  )}
                  {tab === 1 && (
                    <>
                      <div className="palette-label">配色</div>
                      <div className="palette-row">
                        <div className="palette-group">
                          {presets.map(([name, h], i) => (
                            <button
                              key={name}
                              className={
                                "palette " +
                                (Math.abs(settings.hue - h) < 0.001
                                  ? "chosen"
                                  : "")
                              }
                              aria-label={name}
                              data-tooltip={name}
                              style={
                                {
                                  "--preset": cssColor(
                                    schemes[i]?.normal || "#FF496DBF",
                                  ),
                                  "--preset-light": cssColor(
                                    schemes[i]?.highlight || "#FFA0CCEE",
                                  ),
                                } as React.CSSProperties
                              }
                              onClick={() => send("palette", { hue: h })}
                            >
                              <span className="tooltip" aria-hidden="true">
                                {name}
                              </span>
                            </button>
                          ))}
                        </div>
                        <button
                          className={
                            "disclosure compact " +
                            (customOpen ? "expanded" : "")
                          }
                          aria-expanded={customOpen}
                          onClick={() => setCustom(!custom)}
                        >
                          <span>自定义</span>
                          <Icon name="chevron" />
                        </button>
                      </div>
                      <div
                        className={"expansion " + (customOpen ? "open" : "")}
                        aria-hidden={!customOpen}
                        inert={!customOpen}
                      >
                        <div>
                          <div
                            className="hue"
                            style={
                              {
                                "--hue-band": `linear-gradient(to right,${band.map(cssColor).join(",")})`,
                                "--thumb-color": cssColor(settings.normal),
                              } as React.CSSProperties
                            }
                          >
                            <input
                              aria-label="配色色相"
                              type="range"
                              min={0}
                              max={360}
                              step={0.1}
                              value={Math.max(0, settings.hue)}
                              onChange={(e) => {
                                setCustom(true);
                                send("palette", {
                                  hue: Number(e.target.value),
                                });
                              }}
                            />
                          </div>
                          <Disclosure
                            label="手动设置"
                            right
                            open={manualOpen}
                            onChange={() => setManual(!manual)}
                          >
                            <Row label="歌词颜色">
                              <div className="manual-colors">
                                <span>未唱</span>
                                {color("normal", "未唱歌词颜色")}
                                <span>已唱</span>
                                {color("highlight", "已唱歌词颜色")}
                              </div>
                            </Row>
                          </Disclosure>
                        </div>
                      </div>
                      <div className="separator" />
                      <Row label="文字阴影">
                        <Toggle
                          label="文字阴影"
                          value={settings.shadow}
                          onChange={() => patch({ shadow: !settings.shadow })}
                        />
                      </Row>
                      <div
                        className={
                          "expansion " + (settings.shadow ? "open" : "")
                        }
                        aria-hidden={!settings.shadow}
                        inert={!settings.shadow}
                      >
                        <div>
                          <Row label="阴影颜色">
                            {color("shadow", "阴影颜色")}
                          </Row>
                        </div>
                      </div>
                    </>
                  )}
                  {tab === 2 && (
                    <>
                      <Row label="自动适应">
                        <Toggle
                          label="自动适应任务栏空间"
                          value={settings.autoLayout !== false}
                          onChange={() =>
                            patch({ autoLayout: settings.autoLayout === false })
                          }
                        />
                      </Row>
                      {settingSlider("垂直位置", "verticalOffset", -2, -10, 10)}
                      {settings.autoLayout === false && (
                        <>
                          {settingSlider("左边距", "leftMargin", 8, 0, 80)}
                          {settingSlider("右边距", "rightMargin", 40, 0, 200)}
                          {settingSlider(
                            "最小宽度",
                            "minWidth",
                            750,
                            200,
                            1800,
                            "px",
                            50,
                          )}
                          {settingSlider(
                            "最大宽度",
                            "maxWidth",
                            1350,
                            300,
                            2400,
                            "px",
                            50,
                          )}
                        </>
                      )}
                      {s.layoutMeasured === false && (
                        <>
                          <p className="caption">未能检测空闲区域</p>
                          {settingSlider(
                            "手动设置宽度",
                            "fallbackWidthPercent",
                            31,
                            20,
                            69,
                            "%",
                            1,
                          )}
                        </>
                      )}
                    </>
                  )}
                </SlidingContent>
              </div>
            </section>
          )}
          {page === 2 && (
            <section className="card timing">
              <div className="card-scroll">
                <div className="compact-track">
                  <h2>{s.title}</h2>
                  <small>{s.artist}</small>
                </div>
                {Preview({ currentOnly: true })}
                <div className="timing-playback">
                  <Transport s={s} />
                  <Progress
                    s={s}
                    position={position}
                    onPosition={setPositionNow}
                  />
                  <small>
                    {stamp(position)} / {stamp(s.duration)}
                  </small>
                </div>
                <div className="separator" />
                {calibrate(false)}
                <div className="separator" />
                {calibrate(true)}
              </div>
            </section>
          )}
          {page === 3 && (
            <section className="card settings">
              <div className="card-scroll">
                <Row label="主题" guide>
                  <Segments
                    items={["浅色", "深色", "跟随系统"]}
                    value={["light", "dark", "system"].indexOf(settings.theme)}
                    onChange={(i) =>
                      patch({ theme: ["light", "dark", "system"][i] })
                    }
                  />
                </Row>
                <Row label="开机启动" guide>
                  <Toggle
                    label="开机启动"
                    value={!!settings.startup}
                    onChange={() => patch({ startup: !settings.startup })}
                  />
                </Row>
                <Row label="界面动画" guide>
                  <Toggle
                    label="界面动画"
                    value={settings.animations !== false}
                    onChange={() =>
                      patch({ animations: settings.animations === false })
                    }
                  />
                </Row>
                <div className="separator" />
                <div className="section-head data-head">
                  <span>数据管理</span>
                  <button
                    className="text-button"
                    onClick={() => send("refreshData")}
                  >
                    刷新
                  </button>
                </div>
                <Row label="歌词缓存" guide>
                  <span className="caption">
                    {s.cacheCount ?? 0} 首 · {s.cacheSize || "0 B"}
                  </span>
                  <button
                    className="text-button"
                    onClick={() => send("openData")}
                  >
                    打开目录
                  </button>
                </Row>
                <Row label="手动匹配" guide>
                  <span className="caption readout">
                    {s.manualCount ??
                      Object.keys(
                        (settings as unknown as { manual?: object }).manual ||
                          {},
                      ).length}{" "}
                    首
                  </span>
                </Row>
                <Row label="单曲校准" guide>
                  <span className="caption readout">
                    {s.offsetCount ??
                      Object.values(settings.trackOffsets).filter(
                        (v) => v !== 0,
                      ).length}{" "}
                    首
                  </span>
                </Row>
                <div className="separator" />
                <Disclosure
                  label="重置与清理"
                  open={maintenance}
                  onChange={() => setMaintenance(!maintenance)}
                >
                  <Row label="配色" guide>
                    <button
                      className="text-button"
                      onClick={() => send("resetColors")}
                    >
                      恢复默认
                    </button>
                  </Row>
                  <Row label="外观" guide>
                    <button
                      className="text-button"
                      onClick={() => send("resetAppearance")}
                    >
                      恢复默认
                    </button>
                  </Row>
                  <div className="separator" />
                  {[
                    ["歌词缓存", "ClearLyrics"],
                    ["手动匹配（当前歌曲）", "ClearCurrentManual"],
                    ["手动匹配（全部歌曲）", "ClearAllManual"],
                    ["单曲校准（全部歌曲）", "ClearOffsets"],
                  ].map(([label, operation]) => (
                    <Row key={operation} label={label} guide>
                      <button
                        className="text-button danger"
                        disabled={operation === "ClearCurrentManual" && !s.key}
                        onClick={() => setConfirm({ label, operation })}
                      >
                        清除
                      </button>
                    </Row>
                  ))}
                </Disclosure>
              </div>
            </section>
          )}
          {page === 4 && (
            <section className="card about">
              <div className="card-scroll">
                <div className="about-brand">
                  <Logo />
                  <div>
                    <h2>TaskbarLyrics</h2>
                    <p>QQ 音乐任务栏歌词</p>
                  </div>
                </div>
                <div className="separator" />
                <Row label="版本">
                  <span className="caption">{s.version || "1.0.0"}</span>
                </Row>
                <Row label="歌词来源">
                  <span className="caption">QQ 音乐</span>
                </Row>
              </div>
            </section>
          )}
        </div>
        </SlidingContent>
        {s.saveError && (
          <div className="save-error">
            {s.saveError}
            <button className="text-button" onClick={() => send("retrySave")}>
              重试
            </button>
          </div>
        )}
      </main>
      <Presence open={searchOpen} className="modal-backdrop">
          <section
            className="modal"
            role="dialog"
            aria-modal="true"
            aria-label="选择歌词"
          >
            <div className="modal-title">
              <h2>{searchContext.title || "当前没有可匹配的歌曲"}</h2>
              <button
                className="icon-button"
                aria-label="关闭选择歌词"
                onClick={() => setSearchOpen(false)}
              >
                <Icon name="close" />
              </button>
            </div>
            <p className="search-subtitle">
              {searchContext.artist} · {stamp(searchContext.duration)}
            </p>
            <div className="search-bar">
              {queryEditing ? (
                <input
                  autoFocus
                  aria-label="歌曲搜索"
                  value={query}
                  onChange={(e) => setQuery(e.target.value)}
                  onKeyDown={(e) => {
                    if (e.key === "Enter") search();
                  }}
                  onBlur={() => setQueryEditing(false)}
                />
              ) : (
                <button
                  className="search-query"
                  onClick={() => setQueryEditing(true)}
                >
                  {query || "输入歌曲或歌手"}
                </button>
              )}
              <button
                className="icon-button search-button"
                aria-label="搜索"
                disabled={searching}
                onClick={() => search()}
              >
                <Icon name="search" />
              </button>
            </div>
            {searchContext.key !== s.key && (
              <div className="search-stale">
                <span>歌曲已切换</span>
                <button className="text-button" onClick={openSearch}>
                  匹配当前歌曲
                </button>
              </div>
            )}
            {searchError && (
              <div className="search-error" role="status">
                {searchError}
              </div>
            )}
            <div className="results">
              {!results.length && (
                <div className="search-empty">
                  {searching
                    ? "搜索中…"
                    : searched
                      ? "未找到歌词版本"
                      : "搜索歌名或歌手，选择歌词版本"}
                </div>
              )}
              {results.map((c) => (
                <button
                  className={
                    "result " + (selected?.mid === c.mid ? "selected" : "")
                  }
                  key={c.mid}
                  onClick={() => setSelected(c)}
                >
                  <strong>
                    {c.title}
                    <small className="candidate-confidence">
                      {(
                        {
                          High: "推荐",
                          Medium: "核对版本",
                          Low: "低匹配",
                          Rejected: "低匹配",
                        } as Record<string, string>
                      )[
                        (c as Candidate & { confidence?: string }).confidence ||
                          ""
                      ] || ""}
                    </small>
                  </strong>
                  <span>{[c.artist, c.album].filter(Boolean).join(" · ")}</span>
                  <small>
                    {stamp(c.duration * 1000)}
                    {(c as Candidate & { version?: string }).version
                      ? " · " + (c as Candidate & { version?: string }).version
                      : ""}
                  </small>
                </button>
              ))}
            </div>
            <div className="search-status">
              <span>
                {searching
                  ? "搜索中…"
                  : results.length
                    ? `${results.length} 个歌词版本`
                    : ""}
              </span>
              <button
                className="text-button"
                disabled={searching || !hasMore}
                onClick={() => search(true)}
              >
                更多
              </button>
            </div>
            <div className="modal-actions">
              <button
                className="text-button"
                disabled={applying}
                onClick={() => setSearchOpen(false)}
              >
                取消
              </button>
              <button
                className="text-button primary"
                disabled={
                  applying || !selected || !s.key || searchContext.key !== s.key
                }
                onClick={() => {
                  send("apply", {
                    candidate: selected,
                    key: searchContext.key,
                  });
                  setApplying(true);
                  setSearchError("");
                }}
              >
                {applying ? "应用中…" : "应用"}
              </button>
            </div>
          </section>
      </Presence>
      <Presence open={!!confirm} className="modal-backdrop">
        {confirm && (
          <section
            className="confirm-dialog"
            role="alertdialog"
            aria-modal="true"
          >
            <h2>清除{confirm.label}？</h2>
            <p>此操作无法撤销。</p>
            <div className="modal-actions">
              <button className="text-button" onClick={() => setConfirm(null)}>
                取消
              </button>
              <button
                className="text-button danger"
                onClick={() => {
                  send("clearData", { operation: confirm.operation });
                  setConfirm(null);
                }}
              >
                清除
              </button>
            </div>
          </section>
        )}
      </Presence>
    </div>
  );
}
createRoot(document.getElementById("root")!).render(<App />);
