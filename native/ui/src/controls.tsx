import React, { useEffect, useLayoutEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { cssColor } from "./bridge";
import logo from "./logo.svg";

// Keep a closing surface mounted until its exit completes. A quick reopen cancels
// the removal, and the departing controls cannot receive input.
export function Presence({ open, className, children }: {
  open: boolean; className: string; children: React.ReactNode;
}) {
  const [shown, setShown] = useState(open);
  const last = useRef(children);
  useLayoutEffect(() => { if (open) last.current = children; }, [open, children]);
  useLayoutEffect(() => {
    if (open) setShown(true);
    else {
      const timer = setTimeout(() => setShown(false), 180);
      return () => clearTimeout(timer);
    }
  }, [open]);
  if (!open && !shown) return null;
  return <div className={className + (open ? " entering" : " leaving")}
    inert={!open} aria-hidden={!open}>{open ? children : last.current}</div>;
}

export function SlidingContent({ value, direction, children, axis = "x" }: {
  value: number; direction: number; children: React.ReactNode; axis?: "x" | "y";
}) {
  const previous = useRef({ value, children });
  const [changed, setChanged] = useState(false);
  const [exit, setExit] = useState<{ value: number; children: React.ReactNode } | null>(null);
  useLayoutEffect(() => {
    if (previous.current.value !== value) {
      setExit(previous.current);
      setChanged(true);
    }
    previous.current = { value, children };
  }, [value, children]);
  useEffect(() => {
    if (!exit) return;
    const timer = setTimeout(() => setExit(null), axis === "y" ? 240 : 180);
    return () => clearTimeout(timer);
  }, [exit, axis]);
  return <div className={"sliding-content" + (axis === "y" ? " page-transition" : "")} style={{
    "--slide-x": axis === "x" ? `${direction * 16}px` : "0px",
    "--slide-y": axis === "y" ? `${direction * 24}px` : "0px",
    "--slide-duration": axis === "y" ? "240ms" : "180ms",
  } as React.CSSProperties}>
    {exit && <div key={`out-${exit.value}`} className="tab-content departing" inert aria-hidden="true">{exit.children}</div>}
    <div key={value} className={"tab-content " + (changed ? "arriving" : "settled")}>{children}</div>
  </div>;
}

export function Icon({ name }: { name: string }) {
  const media: Record<string, string> = {
    previous: "M3 3H5V15H3Z M15 3L7 9L15 15Z",
    next: "M3 3L11 9L3 15Z M13 3H15V15H13Z",
    play: "M5 3L16 9L5 15Z",
    pause: "M5 3H8V15H5Z M10 3H13V15H10Z",
  };
  if (media[name]) return <svg className="media-icon" viewBox="0 0 18 18"
    fill="currentColor" stroke="none" aria-hidden="true"><path d={media[name]} /></svg>;
  const glyphs: Record<string, string> = {
    music: "\uE8D6",
    palette: "\uE790",
    clock: "\uE823",
    settings: "\uE713",
    info: "\uE946",
    screen: "\uE7F4",
    left: "\uE8E4",
    center: "\uE8E3",
    right: "\uE8E2",
  };
  if (glyphs[name])
    return (
      <span className="mdl-icon" aria-hidden="true">
        {glyphs[name]}
      </span>
    );
  const p: Record<string, React.ReactNode> = {
    chevron: <path d="M3 1 7 5 3 9" />,
    minimize: <path d="M6 12h12" />,
    maximize: <rect x="6" y="6" width="12" height="12" />,
    close: <path d="m6 6 12 12M18 6 6 18" />,
    search: (
      <>
        <circle cx="10" cy="10" r="6" />
        <path d="m15 15 5 5" />
      </>
    ),
  };
  return (
    <svg
      viewBox={name === "chevron" ? "0 0 10 10" : "0 0 24 24"}
      fill="none"
      stroke="currentColor"
      strokeWidth="1.6"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      {p[name]}
    </svg>
  );
}
export function Logo() {
  return <img className="logo" src={logo} alt="" aria-hidden="true" />;
}
export function Toggle({
  value,
  onChange,
  label,
}: {
  value: boolean;
  onChange: () => void;
  label: string;
}) {
  return (
    <button
      className={"toggle " + (value ? "on" : "")}
      role="switch"
      aria-checked={value}
      aria-label={label}
      onClick={onChange}
    >
      <i />
    </button>
  );
}
export function Row({
  label,
  children,
  guide = false,
}: {
  label: string;
  children: React.ReactNode;
  guide?: boolean;
}) {
  return (
    <div className={"row " + (guide ? "guided" : "")}>
      <span>{label}</span>
      {guide && <i className="row-guide" />}
      <div className="row-controls">{children}</div>
    </div>
  );
}
export function Segments({
  items,
  value,
  onChange,
}: {
  items: React.ReactNode[];
  value: number;
  onChange: (i: number) => void;
}) {
  return (
    <div
      className="segments"
      role="tablist"
      style={
        { "--count": items.length, "--index": value } as React.CSSProperties
      }
    >
      <span className="indicator" />
      {items.map((item, i) => (
        <button
          key={i}
          role="tab"
          aria-selected={value === i}
          onClick={() => onChange(i)}
        >
          {item}
        </button>
      ))}
    </div>
  );
}
export function Disclosure({
  label,
  open,
  onChange,
  children,
  right = false,
}: {
  label: string;
  open: boolean;
  onChange: () => void;
  children: React.ReactNode;
  right?: boolean;
}) {
  const content = useRef<HTMLDivElement>(null);
  const [height, setHeight] = useState(0);
  useLayoutEffect(() => {
    const element = content.current;
    if (!element) return;
    const measure = () => setHeight(element.getBoundingClientRect().height);
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(element);
    return () => observer.disconnect();
  }, []);
  return (
    <>
      <button
        className={
          "disclosure " +
          (right ? "disclosure-right " : "") +
          (open ? "expanded" : "")
        }
        aria-expanded={open}
        onClick={onChange}
      >
        <span>{label}</span>
        <Icon name="chevron" />
      </button>
      <div
        className={"expansion " + (open ? "open" : "")}
        style={{ height: open ? height : 0 }}
        aria-hidden={!open}
        inert={!open}
      >
        <div ref={content}>{children}</div>
      </div>
    </>
  );
}
export function NumberField({
  value,
  onChange,
  min,
  max,
  step = 1,
  unit,
  label,
  disabled = false,
}: {
  value: number;
  onChange: (v: number) => void;
  min: number;
  max: number;
  step?: number;
  unit: string;
  label: string;
  disabled?: boolean;
}) {
  const [text, setText] = useState(String(value));
  useEffect(() => setText(String(value)), [value]);
  const commit = () => {
    const n = Number(text);
    if (Number.isFinite(n)) {
      onChange(Math.max(min, Math.min(max, n)));
    } else setText(String(value));
  };
  return (
    <div className="number-field">
      <input
        disabled={disabled}
        inputMode="decimal"
        aria-label={label}
        value={text}
        onChange={(e) => setText(e.target.value)}
        onBlur={commit}
        onKeyDown={(e) => {
          if (e.key === "Enter") {
            commit();
            e.currentTarget.blur();
          }
          if (e.key === "ArrowUp" || e.key === "ArrowDown") {
            e.preventDefault();
            onChange(
              Math.max(
                min,
                Math.min(max, value + (e.key === "ArrowUp" ? step : -step)),
              ),
            );
          }
        }}
      />
      <small>{unit}</small>
    </div>
  );
}

// Portals remain inside the application theme and are constrained to the host viewport.
function Popup({
  anchor,
  open,
  children,
  width,
  onClose,
  className = "",
  fitOptions = false,
}: {
  anchor: HTMLElement | null;
  open: boolean;
  children: React.ReactNode;
  width?: number;
  onClose: () => void;
  className?: string;
  fitOptions?: boolean;
}) {
  const [shown, setShown] = useState(open),
    [rect, setRect] = useState({ left: 0, top: 0, width: 260, maxHeight: 320 });
  const root = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (open) setShown(true);
    else {
      const t = setTimeout(() => setShown(false), 150);
      return () => clearTimeout(t);
    }
  }, [open]);
  useLayoutEffect(() => {
    if (!open || !anchor) return;
    const update = () => {
      const r = anchor.getBoundingClientRect();
      let desiredWidth = width ?? r.width;
      if (fitOptions && root.current) {
        const surface = getComputedStyle(root.current);
        const chrome = parseFloat(surface.paddingLeft) + parseFloat(surface.paddingRight) +
          parseFloat(surface.borderLeftWidth) + parseFloat(surface.borderRightWidth);
        const optionWidths = Array.from(root.current.querySelectorAll<HTMLElement>(".choice-item")).map(item => {
          const style = getComputedStyle(item);
          return (item.querySelector<HTMLElement>(".choice-text")?.scrollWidth ?? 0) +
            parseFloat(style.paddingLeft) + parseFloat(style.paddingRight);
        });
        if (optionWidths.length) desiredWidth = Math.ceil(Math.max(...optionWidths) + chrome);
      }
      const w = Math.min(desiredWidth, window.innerWidth - 16);
      const available = window.innerHeight - r.bottom - 12;
      const naturalHeight = Math.min(
        320,
        root.current?.scrollHeight || (className === "color-popup" ? 312 : 320),
      );
      const below =
        className === "color-popup"
          ? available >= naturalHeight || available >= r.top - 16
          : available >= Math.min(100, naturalHeight);
      const height = Math.min(naturalHeight, below ? available : r.top - 16);
      setRect({
        left: Math.max(8, Math.min(fitOptions ? r.left : r.right - w, window.innerWidth - w - 8)),
        top: below ? r.bottom + 4 : Math.max(8, r.top - height - 4),
        width: w,
        maxHeight: Math.min(320, below ? available : r.top - 16),
      });
    };
    update();
    window.addEventListener("resize", update);
    const dismiss = (e: PointerEvent) => {
      if (
        !root.current?.contains(e.target as Node) &&
        !anchor.contains(e.target as Node)
      )
        onClose();
    };
    document.addEventListener("pointerdown", dismiss);
    return () => {
      window.removeEventListener("resize", update);
      document.removeEventListener("pointerdown", dismiss);
    };
  }, [open, anchor, width, onClose, shown, className, fitOptions]);
  useLayoutEffect(() => {
    const surface = root.current;
    if (!shown || !surface) return;
    let cancelled = false;
    const measure = () => {
      if (cancelled) return;
      surface.querySelectorAll<HTMLElement>(".choice-label").forEach(viewport => {
        const text = viewport.querySelector<HTMLElement>(".choice-text");
        if (!text) return;
        const distance = Math.max(0, Math.ceil(text.scrollWidth - viewport.clientWidth));
        text.dataset.overflow = distance > 0 ? "true" : "false";
        text.style.setProperty("--marquee-distance", `${distance}px`);
        text.style.setProperty("--marquee-duration", `${Math.max(2, distance / 32 + 1.2)}s`);
      });
    };
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(surface);
    void document.fonts.ready.then(measure);
    return () => { cancelled = true; observer.disconnect(); };
  }, [shown, rect.width]);
  if (!shown) return null;
  const host = document.querySelector(".app");
  if (!host) return null;
  return createPortal(
    <div
      ref={root}
      className={
        "popup " + className + " " + (open ? "popup-open" : "popup-close")
      }
      style={rect}
    >
      {children}
    </div>,
    host,
  );
}
export function Choice({
  value,
  items,
  onChange,
  label,
  className = "",
  prefix = "",
}: {
  value: string;
  items: string[];
  onChange: (v: string) => void;
  label: string;
  className?: string;
  prefix?: string;
}) {
  const [open, setOpen] = useState(false);
  const anchor = useRef<HTMLButtonElement>(null),
    list = useRef<HTMLDivElement>(null);
  useLayoutEffect(() => {
    if (open) {
      requestAnimationFrame(() => {
        const selected = list.current?.querySelector<HTMLElement>(
          '[aria-selected="true"]',
        );
        if (selected && list.current)
          list.current.scrollTop =
            selected.offsetTop -
            list.current.clientHeight / 2 +
            selected.clientHeight / 2;
      });
    }
  }, [open]);
  return (
    <>
      <button
        ref={anchor}
        className={"choice " + className + " " + (open ? "expanded" : "")}
        aria-label={label}
        aria-haspopup="listbox"
        aria-expanded={open}
        onClick={() => setOpen(!open)}
      >
        <span className="choice-value">
          {prefix ? (
            <>
              <span className="choice-prefix">{prefix.trim()}</span>
              <span className="choice-number">{value}</span>
            </>
          ) : (
            value
          )}
        </span>
        <Icon name="chevron" />
      </button>
      <Popup
        anchor={anchor.current}
        open={open}
        onClose={() => setOpen(false)}
        className={
          "choice-popup " +
          (className === "source-choice" ? "source-popup" : "")
        }
        fitOptions={className === "source-choice"}
      >
        <div
          className="choice-list"
          ref={list}
          role="listbox"
          aria-label={label}
        >
          {items.map((v) => (
            <button
              key={v}
              role="option"
              aria-selected={v === value}
              className="choice-item"
              onClick={() => {
                onChange(v);
                setOpen(false);
              }}
            >
              <span className="choice-label"><span className="choice-text">{v}</span></span>
            </button>
          ))}
        </div>
      </Popup>
    </>
  );
}
export function FontPicker({
  value,
  fonts,
  onChange,
  label,
}: {
  value: string;
  fonts: string[];
  onChange: (v: string) => void;
  label: string;
}) {
  return (
    <Choice
      value={value}
      items={[...new Set([value, ...fonts])].sort((a, b) => a.localeCompare(b))}
      onChange={onChange}
      label={label}
      className="font-choice"
    />
  );
}

function rgbToHsv(argb: string) {
  const h = argb.slice(-6),
    r = parseInt(h.slice(0, 2), 16) / 255,
    g = parseInt(h.slice(2, 4), 16) / 255,
    b = parseInt(h.slice(4, 6), 16) / 255;
  const max = Math.max(r, g, b),
    min = Math.min(r, g, b),
    d = max - min;
  let hue = 0;
  if (d)
    hue =
      max === r
        ? ((g - b) / d) % 6
        : max === g
          ? (b - r) / d + 2
          : (r - g) / d + 4;
  return {
    h: (hue * 60 + 360) % 360,
    s: max ? d / max : 0,
    v: max,
    a: argb.length === 9 ? parseInt(argb.slice(1, 3), 16) / 255 : 1,
  };
}
function hsvToArgb(h: number, s: number, v: number, a: number) {
  const c = v * s,
    x = c * (1 - Math.abs(((h / 60) % 2) - 1)),
    m = v - c;
  const [r, g, b] =
    h < 60
      ? [c, x, 0]
      : h < 120
        ? [x, c, 0]
        : h < 180
          ? [0, c, x]
          : h < 240
            ? [0, x, c]
            : h < 300
              ? [x, 0, c]
              : [c, 0, x];
  return (
    "#" +
    [a, r + m, g + m, b + m]
      .map((n) =>
        Math.round(n * 255)
          .toString(16)
          .padStart(2, "0"),
      )
      .join("")
      .toUpperCase()
  );
}
export function ColorEditor({
  value,
  recent,
  onChange,
  onRemember,
  label,
}: {
  value: string;
  recent: string[];
  onChange: (v: string) => void;
  onRemember: (v: string, previous?: string) => void;
  label: string;
}) {
  const [open, setOpen] = useState(false),
    [hex, setHex] = useState(value),
    [hsv, setHsv] = useState(rgbToHsv(value));
  const anchor = useRef<HTMLButtonElement>(null),
    latest = useRef(value),
    initial = useRef(value);
  const exit = useRef({ open, hex, onRemember });
  exit.current = { open, hex, onRemember };
  useEffect(
    () => () => {
      if (!exit.current.open) return;
      let final = exit.current.hex.toUpperCase();
      if (/^#[0-9A-F]{6}$/.test(final)) final = "#FF" + final.slice(1);
      if (!/^#[0-9A-F]{8}$/.test(final)) final = latest.current;
      if (final !== initial.current)
        exit.current.onRemember(final, initial.current);
    },
    [],
  );
  useEffect(() => {
    latest.current = value;
    setHex(value);
    setHsv(rgbToHsv(value));
  }, [value]);
  const close = () => {
    let final = hex.toUpperCase();
    if (/^#[0-9A-F]{6}$/.test(final)) final = "#FF" + final.slice(1);
    if (!/^#[0-9A-F]{8}$/.test(final)) final = latest.current;
    if (final !== value) onChange(final);
    latest.current = final;
    if (open && final !== initial.current) onRemember(final, initial.current);
    exit.current.open = false;
    setOpen(false);
  };
  const apply = (next: ReturnType<typeof rgbToHsv>) => {
    setHsv(next);
    const v = hsvToArgb(next.h, next.s, next.v, next.a);
    latest.current = v;
    setHex(v);
    onChange(v);
  };
  const commitHex = () => {
    let v = hex.toUpperCase();
    if (/^#[0-9A-F]{6}$/.test(v)) v = "#FF" + v.slice(1);
    if (/^#[0-9A-F]{8}$/.test(v)) {
      onChange(v);
      latest.current = v;
      setHsv(rgbToHsv(v));
      setHex(v);
    } else setHex(value);
  };
  return (
    <>
      <button
        ref={anchor}
        className={"color-swatch " + (open ? "chosen" : "")}
        aria-label={label}
        aria-expanded={open}
        style={{ "--swatch": cssColor(value) } as React.CSSProperties}
        onClick={() => {
          if (open) close();
          else {
            initial.current = value;
            setOpen(true);
          }
        }}
      >
        <i />
      </button>
      <Popup
        anchor={anchor.current}
        open={open}
        onClose={close}
        width={280}
        className="color-popup"
      >
        <div
          className="spectrum"
          style={{ backgroundColor: `hsl(${hsv.h} 100% 50%)` }}
          onPointerDown={(e) => {
            e.currentTarget.setPointerCapture(e.pointerId);
            const r = e.currentTarget.getBoundingClientRect();
            apply({
              ...hsv,
              s: Math.max(0, Math.min(1, (e.clientX - r.left) / r.width)),
              v: Math.max(0, Math.min(1, 1 - (e.clientY - r.top) / r.height)),
            });
          }}
          onPointerMove={(e) => {
            if (!e.currentTarget.hasPointerCapture(e.pointerId)) return;
            const r = e.currentTarget.getBoundingClientRect();
            apply({
              ...hsv,
              s: Math.max(0, Math.min(1, (e.clientX - r.left) / r.width)),
              v: Math.max(0, Math.min(1, 1 - (e.clientY - r.top) / r.height)),
            });
          }}
        >
          <i
            style={{ left: `${hsv.s * 100}%`, top: `${(1 - hsv.v) * 100}%` }}
          />
        </div>
        <input
          className="spectrum-hue"
          aria-label="颜色色相"
          type="range"
          min={0}
          max={360}
          value={hsv.h}
          onChange={(e) => apply({ ...hsv, h: Number(e.target.value) })}
        />
        <div className="hex-row">
          <span>颜色值</span>
          <input
            aria-label={label + "颜色值"}
            spellCheck={false}
            maxLength={9}
            value={hex}
            onChange={(e) => setHex(e.target.value)}
            onBlur={commitHex}
            onKeyDown={(e) => {
              if (e.key === "Enter") {
                commitHex();
                e.currentTarget.blur();
              }
            }}
          />
        </div>
        <div className="alpha-row">
          <span>不透明度</span>
          <input
            aria-label="不透明度"
            type="range"
            min={0}
            max={100}
            value={hsv.a * 100}
            onChange={(e) => apply({ ...hsv, a: Number(e.target.value) / 100 })}
          />
        </div>
        {recent.length > 0 && (
          <div className="recent-colors">
            {recent.slice(0, 6).map((c, i) => (
              <button
                key={c + i}
                aria-label={"最近颜色 " + c}
                style={{ "--swatch": cssColor(c) } as React.CSSProperties}
                onClick={() => {
                  onChange(c);
                  latest.current = c;
                  setHsv(rgbToHsv(c));
                  setHex(c);
                }}
              >
                <i />
              </button>
            ))}
          </div>
        )}
      </Popup>
    </>
  );
}
