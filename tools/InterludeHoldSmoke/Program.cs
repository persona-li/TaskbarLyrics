using TaskbarLyrics.Core.Lyrics;
using TaskbarLyrics.Core.Models;

var failed = 0;
void Assert(bool cond, string name)
{
    if (cond)
    {
        Console.WriteLine("OK   " + name);
    }
    else
    {
        Console.WriteLine("FAIL " + name);
        failed++;
    }
}

// Real QRC-like lines from product report
var line1 = new QrcLine
{
    StartMs = 54155,
    DurationMs = 1976, // End = 56131
    Text = "You make me feel so high",
    Words = new List<QrcWord>
    {
        new() { Text = "You", StartMs = 54155, DurationMs = 177 },
        new() { Text = "make", StartMs = 54332, DurationMs = 193 },
        new() { Text = "me", StartMs = 54525, DurationMs = 240 },
        new() { Text = "feel", StartMs = 54765, DurationMs = 443 },
        new() { Text = "so", StartMs = 55208, DurationMs = 450 },
        new() { Text = "high", StartMs = 55658, DurationMs = 473 },
    }
};
var line2 = new QrcLine
{
    StartMs = 56349,
    DurationMs = 3544, // End = 59893
    Text = "I'm so crazy",
    Words = new List<QrcWord>
    {
        new() { Text = "I'm", StartMs = 56349, DurationMs = 186 },
        new() { Text = "so", StartMs = 56535, DurationMs = 218 },
        new() { Text = "crazy", StartMs = 56753, DurationMs = 1302 },
    }
};
var line3 = new QrcLine
{
    StartMs = 60857,
    DurationMs = 3922, // End = 64779
    Text = "막 끌려 더",
};
var line4 = new QrcLine
{
    StartMs = 65532,
    DurationMs = 1601, // End = 67133
    Text = "I'm feeling so energetic",
};

var lines = new List<QrcLine> { line1, line2, line3, line4 };
var sync = new LyricsSynchronizer();

// Gap1 = 56349 - 56131 = 218ms (short)
// Gap2 = 60857 - 59893 = 964ms (short)
// Gap3 = 65532 - 64779 = 753ms (short)

// ── Test 1: 218ms gap ──
Assert(sync.Resolve(lines, TimeSpan.FromMilliseconds(56131)).LineIndex == 0, "218: at end still L0");
Assert(sync.Resolve(lines, TimeSpan.FromMilliseconds(56200)).LineIndex == 0, "218: 56200 hold L0");
Assert(sync.Resolve(lines, TimeSpan.FromMilliseconds(56348)).LineIndex == 0, "218: 56348 hold L0");
Assert(sync.Resolve(lines, TimeSpan.FromMilliseconds(56349)).LineIndex == 1, "218: 56349 → L1");

// ── Test 2: 964ms gap — no blank (old 500ms would blank after 60393) ──
Assert(sync.Resolve(lines, TimeSpan.FromMilliseconds(59893)).LineIndex == 1, "964: end L1");
Assert(sync.Resolve(lines, TimeSpan.FromMilliseconds(60000)).LineIndex == 1, "964: 60000 hold L1");
Assert(sync.Resolve(lines, TimeSpan.FromMilliseconds(60393)).LineIndex == 1, "964: 60393 hold L1 (was blank)");
Assert(sync.Resolve(lines, TimeSpan.FromMilliseconds(60500)).LineIndex == 1, "964: 60500 hold L1");
Assert(sync.Resolve(lines, TimeSpan.FromMilliseconds(60856)).LineIndex == 1, "964: 60856 hold L1");
Assert(sync.Resolve(lines, TimeSpan.FromMilliseconds(60857)).LineIndex == 2, "964: 60857 → L2");

// ── Test 3: 753ms gap ──
Assert(sync.Resolve(lines, TimeSpan.FromMilliseconds(64779)).LineIndex == 2, "753: end L2");
Assert(sync.Resolve(lines, TimeSpan.FromMilliseconds(65000)).LineIndex == 2, "753: hold L2");
Assert(sync.Resolve(lines, TimeSpan.FromMilliseconds(65531)).LineIndex == 2, "753: 65531 hold L2");
Assert(sync.Resolve(lines, TimeSpan.FromMilliseconds(65532)).LineIndex == 3, "753: 65532 → L3");

// ── Test 4: long gap 10000ms ──
var longPrev = new QrcLine { StartMs = 99000, DurationMs = 1000, Text = "prev" }; // End 100000
var longNext = new QrcLine { StartMs = 110000, DurationMs = 1000, Text = "next" };
var longLines = new List<QrcLine> { longPrev, longNext };
Assert(sync.Resolve(longLines, TimeSpan.FromMilliseconds(100000)).LineIndex == 0, "long: at end");
Assert(sync.Resolve(longLines, TimeSpan.FromMilliseconds(101000)).LineIndex == 0, "long: 101000 hold");
Assert(sync.Resolve(longLines, TimeSpan.FromMilliseconds(101500)).LineIndex == 0, "long: 101500 hold");
Assert(sync.Resolve(longLines, TimeSpan.FromMilliseconds(101501)).LineIndex < 0, "long: 101501 empty");
Assert(sync.Resolve(longLines, TimeSpan.FromMilliseconds(109999)).LineIndex < 0, "long: 109999 empty");
Assert(sync.Resolve(longLines, TimeSpan.FromMilliseconds(110000)).LineIndex == 1, "long: 110000 next");

// ── Test 5: Gap == 2000 hold until next ──
var g2kA = new QrcLine { StartMs = 0, DurationMs = 1000, Text = "A" }; // End 1000
var g2kB = new QrcLine { StartMs = 3000, DurationMs = 500, Text = "B" }; // gap 2000
var g2k = new List<QrcLine> { g2kA, g2kB };
Assert(sync.Resolve(g2k, TimeSpan.FromMilliseconds(2999)).LineIndex == 0, "gap2000: hold until next");
Assert(sync.Resolve(g2k, TimeSpan.FromMilliseconds(3000)).LineIndex == 1, "gap2000: next starts");

// ── Test 6: Gap == 2001 long hold 1500 then blank ──
var g2001A = new QrcLine { StartMs = 0, DurationMs = 1000, Text = "A" }; // End 1000
var g2001B = new QrcLine { StartMs = 3001, DurationMs = 500, Text = "B" }; // gap 2001
var g2001 = new List<QrcLine> { g2001A, g2001B };
Assert(sync.Resolve(g2001, TimeSpan.FromMilliseconds(2500)).LineIndex == 0, "gap2001: 1500 hold");
Assert(sync.Resolve(g2001, TimeSpan.FromMilliseconds(2501)).LineIndex < 0, "gap2001: blank after 1500");
Assert(sync.Resolve(g2001, TimeSpan.FromMilliseconds(3001)).LineIndex == 1, "gap2001: next");

// ── Test 7: last line ──
Assert(sync.Resolve(lines, TimeSpan.FromMilliseconds(67133)).LineIndex == 3, "last: at end");
Assert(sync.Resolve(lines, TimeSpan.FromMilliseconds(68633)).LineIndex == 3, "last: +1500 hold");
Assert(sync.Resolve(lines, TimeSpan.FromMilliseconds(68634)).LineIndex < 0, "last: after long hold empty");

// ── Test 8–9: completed word state in short hold ──
var hold = sync.Resolve(lines, TimeSpan.FromMilliseconds(56200));
Assert(hold.LineIndex == 0, "KTV hold: line 0");
Assert(hold.WordProgress >= 0.999, "KTV hold: WordProgress 1.0");
Assert(hold.WordIndex == 5 && hold.CurrentWord == "high", "KTV hold: last word high");
Assert(hold.InInterlude, "KTV hold: InInterlude");

// During active line mid-word then after end stays 1.0
var mid = sync.Resolve(lines, TimeSpan.FromMilliseconds(56000));
Assert(mid.WordIndex == 5 && mid.WordProgress < 1.0, "KTV mid high progress");
var afterEnd = sync.Resolve(lines, TimeSpan.FromMilliseconds(56300));
Assert(afterEnd.WordProgress >= 0.999 && afterEnd.WordIndex == 5, "KTV after end stays complete");

// Before first line
Assert(sync.Resolve(lines, TimeSpan.FromMilliseconds(100)).LineIndex < 0, "before first empty");

Console.WriteLine(failed == 0 ? "\nALL PASSED" : $"\n{failed} FAILED");
return failed == 0 ? 0 : 1;
