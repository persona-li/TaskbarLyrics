using TaskbarLyrics.Core.Lyrics;
using TaskbarLyrics.Core.Models;
using TaskbarLyrics.Core.Playback;

// Deterministic smoke tests for seek / pause-settling / candidate / resume.
var failed = 0;
void Assert(bool cond, string name)
{
    if (!cond)
    {
        Console.WriteLine("FAIL " + name);
        failed++;
    }
    else
    {
        Console.WriteLine("OK   " + name);
    }
}

// ── A: Playing seek (+400ms) ──
{
    var clock = new PlaybackClock();
    var proc = new TimelineSampleProcessor(clock);
    var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    proc.Process(new GsmtcTimelineSample(
        TimeSpan.FromSeconds(10), t0, t0, PlaybackStatus.Playing, 1.0, TimelineSampleSource.Poll));
    proc.Process(new GsmtcTimelineSample(
        TimeSpan.FromSeconds(10.5), t0.AddMilliseconds(100), t0.AddMilliseconds(100),
        PlaybackStatus.Playing, 1.0, TimelineSampleSource.TimelineEvent));
    Assert(Math.Abs(clock.GetEstimatedPosition().TotalMilliseconds - 10500) < 50,
        "Seek +400ms hard reanchor");
}

// ── B: pause / resume stale gate ──
{
    var clock = new PlaybackClock();
    var proc = new TimelineSampleProcessor(clock);
    var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    proc.Process(new GsmtcTimelineSample(
        TimeSpan.FromSeconds(30), t0, t0, PlaybackStatus.Playing, 1.0, TimelineSampleSource.Poll));
    proc.Process(new GsmtcTimelineSample(
        TimeSpan.FromSeconds(30), t0.AddMilliseconds(50), t0.AddMilliseconds(50),
        PlaybackStatus.Paused, 1.0, TimelineSampleSource.PlaybackInfoEvent));
    var frozen = clock.GetEstimatedPosition();
    proc.Process(new GsmtcTimelineSample(
        TimeSpan.FromSeconds(30), t0, t0.AddSeconds(2), PlaybackStatus.Playing, 1.0,
        TimelineSampleSource.PlaybackInfoEvent));
    Assert(Math.Abs((clock.GetEstimatedPosition() - frozen).TotalMilliseconds) < 80,
        "Resume ignores stale timeline");
}

// ── C: out-of-order ──
{
    var clock = new PlaybackClock();
    var proc = new TimelineSampleProcessor(clock);
    var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    proc.Process(new GsmtcTimelineSample(
        TimeSpan.FromSeconds(5), t0, t0, PlaybackStatus.Playing, 1.0, TimelineSampleSource.Poll));
    proc.Process(new GsmtcTimelineSample(
        TimeSpan.FromSeconds(20), t0.AddSeconds(1), t0.AddSeconds(1), PlaybackStatus.Playing, 1.0,
        TimelineSampleSource.TimelineEvent));
    var afterSeek = clock.GetEstimatedPosition().TotalSeconds;
    proc.Process(new GsmtcTimelineSample(
        TimeSpan.FromSeconds(5.1), t0.AddMilliseconds(100), t0.AddSeconds(2), PlaybackStatus.Playing, 1.0,
        TimelineSampleSource.TimelineEvent));
    Assert(Math.Abs(clock.GetEstimatedPosition().TotalSeconds - afterSeek) < 0.05,
        "Out-of-order position rejected");
}

// ── D: confirmed paused seek (needs 2 fresh evidences) ──
{
    var clock = new PlaybackClock();
    var proc = new TimelineSampleProcessor(clock);
    var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    proc.Process(new GsmtcTimelineSample(
        TimeSpan.FromSeconds(40), t0, t0, PlaybackStatus.Playing, 1.0, TimelineSampleSource.Poll));
    proc.Process(new GsmtcTimelineSample(
        TimeSpan.FromSeconds(40), t0.AddMilliseconds(10), t0.AddMilliseconds(10),
        PlaybackStatus.Paused, 1.0, TimelineSampleSource.PlaybackInfoEvent));
    // Single sample — must NOT reanchor yet
    proc.Process(new GsmtcTimelineSample(
        TimeSpan.FromSeconds(90), t0.AddMilliseconds(20), t0.AddMilliseconds(20),
        PlaybackStatus.Paused, 1.0, TimelineSampleSource.TimelineEvent));
    Assert(Math.Abs(clock.GetEstimatedPosition().TotalSeconds - 40) < 0.05,
        "Single far sample does not HardReanchor");
    // Second fresh evidence near 90s
    Thread.Sleep(90); // >= MinConfirmAgeMs
    proc.Process(new GsmtcTimelineSample(
        TimeSpan.FromSeconds(90.03), t0.AddMilliseconds(200), t0.AddMilliseconds(200),
        PlaybackStatus.Paused, 1.0, TimelineSampleSource.TimelineEvent));
    Assert(Math.Abs(clock.GetEstimatedPosition().TotalSeconds - 90.03) < 0.05,
        "Confirmed paused seek → latest ~90.03s");
    Assert(!clock.GetSnapshot().IsPlaying, "Still paused after confirmed seek");
}

// ── E: 108600 settle (많이도) — never reanchor ──
{
    var clock = new PlaybackClock();
    var proc = new TimelineSampleProcessor(clock);
    var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    proc.Process(new GsmtcTimelineSample(
        TimeSpan.FromMilliseconds(108600), t0, t0, PlaybackStatus.Playing, 1.0, TimelineSampleSource.Poll));
    proc.Process(new GsmtcTimelineSample(
        TimeSpan.FromMilliseconds(108600), t0.AddMilliseconds(30), t0.AddMilliseconds(30),
        PlaybackStatus.Paused, 1.0, TimelineSampleSource.PlaybackInfoEvent));
    var frozen = clock.GetEstimatedPosition().TotalMilliseconds;
    foreach (var (raw, i) in new[] { 108269.0, 108300.0, 108520.0, 108610.0 }.Select((v, i) => (v, i)))
    {
        var ti = t0.AddMilliseconds(50 * (i + 1));
        proc.Process(new GsmtcTimelineSample(
            TimeSpan.FromMilliseconds(raw), ti, ti, PlaybackStatus.Paused, 1.0,
            TimelineSampleSource.TimelineEvent));
        Assert(Math.Abs(clock.GetEstimatedPosition().TotalMilliseconds - frozen) < 5,
            $"108600 settle ignore {raw}");
    }
}

// ── F: 38240 → 36996 → 38220 (건 / 마주한다는 bug) ──
{
    var line = new QrcLine
    {
        StartMs = 36417,
        DurationMs = 1853,
        Text = "이별을 마주한다는 건",
        Words = new List<QrcWord>
        {
            new() { Text = "이별을", StartMs = 36417, DurationMs = 579 },
            new() { Text = "마주한다는", StartMs = 36996, DurationMs = 1007 },
            new() { Text = "건", StartMs = 38003, DurationMs = 267 },
        }
    };
    var lines = new List<QrcLine> { line };
    var sync = new LyricsSynchronizer();
    var atGeon = sync.Resolve(lines, TimeSpan.FromMilliseconds(38240));
    Assert(atGeon.CurrentWord == "건" || atGeon.WordIndex == 2, "At 38240 word is 건");

    var clock = new PlaybackClock();
    var proc = new TimelineSampleProcessor(clock);
    var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    proc.Process(new GsmtcTimelineSample(
        TimeSpan.FromMilliseconds(38240), t0, t0, PlaybackStatus.Playing, 1.0, TimelineSampleSource.Poll));
    proc.Process(new GsmtcTimelineSample(
        TimeSpan.FromMilliseconds(38240), t0.AddMilliseconds(20), t0.AddMilliseconds(20),
        PlaybackStatus.Paused, 1.0, TimelineSampleSource.PlaybackInfoEvent));
    Assert(Math.Abs(clock.GetEstimatedPosition().TotalMilliseconds - 38240) < 5, "Freeze 38240");

    // Diff 1244ms — would have triggered old 1200 immediate seek; must only start candidate
    var t1 = t0.AddMilliseconds(40);
    proc.Process(new GsmtcTimelineSample(
        TimeSpan.FromMilliseconds(36996), t1, t1, PlaybackStatus.Paused, 1.0,
        TimelineSampleSource.TimelineEvent));
    Assert(Math.Abs(clock.GetEstimatedPosition().TotalMilliseconds - 38240) < 5,
        "38240: first 36996 stays frozen (candidate only)");
    var lyric1 = sync.Resolve(lines, clock.GetEstimatedPosition());
    Assert(lyric1.WordIndex == atGeon.WordIndex, "Lyric still 건 after 36996 candidate");

    // Rollback near frozen cancels candidate
    var t2 = t0.AddMilliseconds(100);
    proc.Process(new GsmtcTimelineSample(
        TimeSpan.FromMilliseconds(38220), t2, t2, PlaybackStatus.Paused, 1.0,
        TimelineSampleSource.TimelineEvent));
    Assert(Math.Abs(clock.GetEstimatedPosition().TotalMilliseconds - 38240) < 5,
        "38240: 38220 cancels candidate, still frozen");
    var lyric2 = sync.Resolve(lines, clock.GetEstimatedPosition());
    Assert(lyric2.WordIndex == atGeon.WordIndex, "Lyric still 건 after cancel");
}

// ── G: real confirmed seek 60000 @T1, 60030 @T2 ──
{
    var clock = new PlaybackClock();
    var proc = new TimelineSampleProcessor(clock);
    var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    proc.Process(new GsmtcTimelineSample(
        TimeSpan.FromMilliseconds(38240), t0, t0, PlaybackStatus.Playing, 1.0, TimelineSampleSource.Poll));
    proc.Process(new GsmtcTimelineSample(
        TimeSpan.FromMilliseconds(38240), t0.AddMilliseconds(10), t0.AddMilliseconds(10),
        PlaybackStatus.Paused, 1.0, TimelineSampleSource.PlaybackInfoEvent));
    var t1 = t0.AddMilliseconds(50);
    proc.Process(new GsmtcTimelineSample(
        TimeSpan.FromMilliseconds(60000), t1, t1, PlaybackStatus.Paused, 1.0,
        TimelineSampleSource.TimelineEvent));
    Assert(Math.Abs(clock.GetEstimatedPosition().TotalMilliseconds - 38240) < 5,
        "Seek #1 candidate only");
    Thread.Sleep(90);
    var t2 = t0.AddMilliseconds(200);
    proc.Process(new GsmtcTimelineSample(
        TimeSpan.FromMilliseconds(60030), t2, t2, PlaybackStatus.Paused, 1.0,
        TimelineSampleSource.TimelineEvent));
    Assert(Math.Abs(clock.GetEstimatedPosition().TotalMilliseconds - 60030) < 5,
        "Seek confirmed → latest 60030");
}

// ── H: duplicate stale poll never confirms ──
{
    var clock = new PlaybackClock();
    var proc = new TimelineSampleProcessor(clock);
    var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    proc.Process(new GsmtcTimelineSample(
        TimeSpan.FromMilliseconds(38240), t0, t0, PlaybackStatus.Playing, 1.0, TimelineSampleSource.Poll));
    proc.Process(new GsmtcTimelineSample(
        TimeSpan.FromMilliseconds(38240), t0.AddMilliseconds(10), t0.AddMilliseconds(10),
        PlaybackStatus.Paused, 1.0, TimelineSampleSource.PlaybackInfoEvent));
    var t1 = t0.AddMilliseconds(40);
    for (var i = 0; i < 5; i++)
    {
        // Same LastUpdated + same Raw — only CapturedAt changes → not new evidence
        // First sample sets identity; subsequent identical pos+lastUpdated are deduped entirely
        // Use Poll with same LastUpdated but slightly different CapturedAt via different path:
        // After first, Process dedups same pos+lastUpdated+status → returns early.
        // So we need same logical timeline but force process... Dedup blocks identical.
        // Spec: Poll 36996 @ T1 three times with same LastUpdated — after first, remaining
        // are deduped at Process entry. Clock stays frozen. Good.
        proc.Process(new GsmtcTimelineSample(
            TimeSpan.FromMilliseconds(36996), t1, t1.AddMilliseconds(i * 50),
            PlaybackStatus.Paused, 1.0, TimelineSampleSource.Poll));
    }

    Assert(Math.Abs(clock.GetEstimatedPosition().TotalMilliseconds - 38240) < 5,
        "Stale poll never confirms seek");
}

// ── I: continuous drag — intermediate positions never applied; final cluster confirms ──
{
    var clock = new PlaybackClock();
    var proc = new TimelineSampleProcessor(clock);
    var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    proc.Process(new GsmtcTimelineSample(
        TimeSpan.FromMilliseconds(38240), t0, t0, PlaybackStatus.Playing, 1.0, TimelineSampleSource.Poll));
    proc.Process(new GsmtcTimelineSample(
        TimeSpan.FromMilliseconds(38240), t0.AddMilliseconds(10), t0.AddMilliseconds(10),
        PlaybackStatus.Paused, 1.0, TimelineSampleSource.PlaybackInfoEvent));

    var drags = new[] { 60000.0, 65000.0, 70000.0, 72000.0 };
    for (var i = 0; i < drags.Length; i++)
    {
        var ti = t0.AddMilliseconds(50 + i * 100);
        Thread.Sleep(20);
        proc.Process(new GsmtcTimelineSample(
            TimeSpan.FromMilliseconds(drags[i]), ti, ti, PlaybackStatus.Paused, 1.0,
            TimelineSampleSource.TimelineEvent));
        Assert(Math.Abs(clock.GetEstimatedPosition().TotalMilliseconds - 38240) < 5,
            $"Drag intermediate {drags[i]} not applied");
    }

    Thread.Sleep(90);
    var tFinal = t0.AddMilliseconds(500);
    proc.Process(new GsmtcTimelineSample(
        TimeSpan.FromMilliseconds(72040), tFinal, tFinal, PlaybackStatus.Paused, 1.0,
        TimelineSampleSource.TimelineEvent));
    // 72000 started cluster; 72040 confirms if within 300ms of 72000
    Assert(Math.Abs(clock.GetEstimatedPosition().TotalMilliseconds - 72040) < 5,
        "Continuous drag confirms latest ~72040");
}

// ── J: Resume cancels open candidate ──
{
    var clock = new PlaybackClock();
    var proc = new TimelineSampleProcessor(clock);
    var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    proc.Process(new GsmtcTimelineSample(
        TimeSpan.FromMilliseconds(50000), t0, t0, PlaybackStatus.Playing, 1.0, TimelineSampleSource.Poll));
    proc.Process(new GsmtcTimelineSample(
        TimeSpan.FromMilliseconds(50000), t0.AddMilliseconds(10), t0.AddMilliseconds(10),
        PlaybackStatus.Paused, 1.0, TimelineSampleSource.PlaybackInfoEvent));
    proc.Process(new GsmtcTimelineSample(
        TimeSpan.FromMilliseconds(60000), t0.AddMilliseconds(40), t0.AddMilliseconds(40),
        PlaybackStatus.Paused, 1.0, TimelineSampleSource.TimelineEvent));
    Assert(Math.Abs(clock.GetEstimatedPosition().TotalMilliseconds - 50000) < 5,
        "Candidate open, frozen 50000");
    var frozen = clock.GetEstimatedPosition();
    proc.Process(new GsmtcTimelineSample(
        TimeSpan.FromMilliseconds(60000), t0.AddMilliseconds(40), t0.AddMilliseconds(300),
        PlaybackStatus.Playing, 1.0, TimelineSampleSource.PlaybackInfoEvent));
    Assert(Math.Abs((clock.GetEstimatedPosition() - frozen).TotalMilliseconds) < 80,
        "Resume cancels candidate, resumes from frozen");
    Assert(clock.GetSnapshot().IsPlaying, "Playing after resume cancel candidate");
}

// ── K: lyric freeze 많이도 ──
{
    var line = new QrcLine
    {
        StartMs = 107539,
        DurationMs = 1903,
        Text = "너에게 참 많이도 배웠다",
        Words = new List<QrcWord>
        {
            new() { Text = "너에게", StartMs = 107539, DurationMs = 319 },
            new() { Text = "참", StartMs = 107858, DurationMs = 411 },
            new() { Text = "많이도", StartMs = 108269, DurationMs = 758 },
            new() { Text = "배웠다", StartMs = 109027, DurationMs = 415 },
        }
    };
    var lines = new List<QrcLine> { line };
    var sync = new LyricsSynchronizer();
    var atMuch = sync.Resolve(lines, TimeSpan.FromMilliseconds(108600));
    var clock = new PlaybackClock();
    var proc = new TimelineSampleProcessor(clock);
    var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    proc.Process(new GsmtcTimelineSample(
        TimeSpan.FromMilliseconds(108600), t0, t0, PlaybackStatus.Playing, 1.0, TimelineSampleSource.Poll));
    proc.Process(new GsmtcTimelineSample(
        TimeSpan.FromMilliseconds(108600), t0.AddMilliseconds(20), t0.AddMilliseconds(20),
        PlaybackStatus.Paused, 1.0, TimelineSampleSource.PlaybackInfoEvent));
    foreach (var raw in new[] { 108269L, 108300L })
    {
        var ti = t0.AddMilliseconds(raw % 500);
        proc.Process(new GsmtcTimelineSample(
            TimeSpan.FromMilliseconds(raw), ti, ti, PlaybackStatus.Paused, 1.0,
            TimelineSampleSource.TimelineEvent));
        Assert(sync.Resolve(lines, clock.GetEstimatedPosition()).WordIndex == atMuch.WordIndex,
            $"많이도 frozen at raw={raw}");
    }
}

// ── L: rapid pause/play ──
{
    var clock = new PlaybackClock();
    var proc = new TimelineSampleProcessor(clock);
    var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    var pos = TimeSpan.FromMilliseconds(60000);
    proc.Process(new GsmtcTimelineSample(pos, t0, t0, PlaybackStatus.Playing, 1.0, TimelineSampleSource.Poll));
    var ok = true;
    for (var i = 0; i < 20; i++)
    {
        var tp = t0.AddMilliseconds(100 + i * 40);
        proc.Process(new GsmtcTimelineSample(pos, tp, tp, PlaybackStatus.Paused, 1.0,
            TimelineSampleSource.PlaybackInfoEvent));
        proc.Process(new GsmtcTimelineSample(
            pos - TimeSpan.FromMilliseconds(200), tp.AddMilliseconds(10), tp.AddMilliseconds(10),
            PlaybackStatus.Paused, 1.0, TimelineSampleSource.TimelineEvent));
        if (Math.Abs((clock.GetEstimatedPosition() - pos).TotalMilliseconds) > 5)
        {
            ok = false;
            break;
        }

        var tr = tp.AddMilliseconds(20);
        proc.Process(new GsmtcTimelineSample(
            pos - TimeSpan.FromMilliseconds(200), tp, tr, PlaybackStatus.Playing, 1.0,
            TimelineSampleSource.PlaybackInfoEvent));
        proc.Process(new GsmtcTimelineSample(
            pos, tr.AddMilliseconds(30), tr.AddMilliseconds(40), PlaybackStatus.Playing, 1.0,
            TimelineSampleSource.TimelineEvent));
        pos = clock.GetEstimatedPosition();
    }

    Assert(ok, "Rapid Pause/Resume ×20");
}

// ── M: PreferredPollIntervalMs policy ──
{
    var clock = new PlaybackClock();
    var proc = new TimelineSampleProcessor(clock);
    var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    Assert(proc.PreferredPollIntervalMs == TimelineConstants.PausedPollMs,
        "Poll policy idle → Paused");
    proc.Process(new GsmtcTimelineSample(
        TimeSpan.FromSeconds(10), t0, t0, PlaybackStatus.Playing, 1.0, TimelineSampleSource.Poll));
    Assert(proc.PreferredPollIntervalMs == TimelineConstants.PlayingStablePollMs,
        "Poll policy Playing → 75ms");
    // Force scrub via seek
    proc.Process(new GsmtcTimelineSample(
        TimeSpan.FromSeconds(60), t0.AddMilliseconds(100), t0.AddMilliseconds(100),
        PlaybackStatus.Playing, 1.0, TimelineSampleSource.Poll));
    Assert(proc.IsScrubbing, "Seek enters scrub");
    Assert(proc.PreferredPollIntervalMs == TimelineConstants.ScrubbingPollMs,
        "Poll policy Scrubbing → 40ms");
    proc.Process(new GsmtcTimelineSample(
        TimeSpan.FromSeconds(60), t0.AddMilliseconds(150), t0.AddMilliseconds(150),
        PlaybackStatus.Paused, 1.0, TimelineSampleSource.PlaybackInfoEvent));
    Assert(proc.PreferredPollIntervalMs == TimelineConstants.PausedPollMs,
        "Poll policy Paused → 200ms");
}

// ── N: CapturedAt forward seek (LastUpdated stuck, Raw jumps) ──
// After accept at 60s, next sample with same LastUpdated and huge Position jump
// is already covered by LastUpdatedΔ==0 branch; additionally CapturedAt path
// when LastUpdated regresses slightly (within OOO tolerance) or no LU advance reliability.
{
    var clock = new PlaybackClock();
    var proc = new TimelineSampleProcessor(clock);
    var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    var c1 = t0;
    proc.Process(new GsmtcTimelineSample(
        TimeSpan.FromMilliseconds(60000), t0, c1, PlaybackStatus.Playing, 1.0, TimelineSampleSource.Poll));
    // Same LastUpdated, Position jump — existing branch
    var c2 = t0.AddMilliseconds(500);
    proc.Process(new GsmtcTimelineSample(
        TimeSpan.FromMilliseconds(120000), t0, c2, PlaybackStatus.Playing, 1.0, TimelineSampleSource.Poll));
    Assert(Math.Abs(clock.GetEstimatedPosition().TotalMilliseconds - 120000) < 50,
        "Forward seek LastUpdated stuck → HardReanchor ~120000");
}

// ── O: CapturedAt-style backward seek (same LastUpdated) ──
{
    var clock = new PlaybackClock();
    var proc = new TimelineSampleProcessor(clock);
    var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    proc.Process(new GsmtcTimelineSample(
        TimeSpan.FromMilliseconds(120000), t0, t0, PlaybackStatus.Playing, 1.0, TimelineSampleSource.Poll));
    proc.Process(new GsmtcTimelineSample(
        TimeSpan.FromMilliseconds(60000), t0, t0.AddMilliseconds(100), PlaybackStatus.Playing, 1.0,
        TimelineSampleSource.Poll));
    Assert(Math.Abs(clock.GetEstimatedPosition().TotalMilliseconds - 60000) < 50,
        "Backward seek → HardReanchor ~60000");
}

// ── P: small playing advance not seek ──
{
    var clock = new PlaybackClock();
    var proc = new TimelineSampleProcessor(clock);
    var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    proc.Process(new GsmtcTimelineSample(
        TimeSpan.FromMilliseconds(60000), t0, t0, PlaybackStatus.Playing, 1.0, TimelineSampleSource.Poll));
    proc.Process(new GsmtcTimelineSample(
        TimeSpan.FromMilliseconds(60080), t0.AddMilliseconds(75), t0.AddMilliseconds(75),
        PlaybackStatus.Playing, 1.0, TimelineSampleSource.Poll));
    var est = clock.GetEstimatedPosition().TotalMilliseconds;
    Assert(est > 60000 && est < 60200, "Small advance not treated as big seek");
}

// ── Q: same raw repeated polls do not re-seek ──
{
    var clock = new PlaybackClock();
    var proc = new TimelineSampleProcessor(clock);
    var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    proc.Process(new GsmtcTimelineSample(
        TimeSpan.FromMilliseconds(60000), t0, t0, PlaybackStatus.Playing, 1.0, TimelineSampleSource.Poll));
    proc.Process(new GsmtcTimelineSample(
        TimeSpan.FromMilliseconds(120000), t0.AddMilliseconds(100), t0.AddMilliseconds(100),
        PlaybackStatus.Playing, 1.0, TimelineSampleSource.Poll));
    var afterSeek = clock.GetEstimatedPosition().TotalMilliseconds;
    // Advance LastUpdated with same ~raw so CapturedAt expected tracks
    for (var i = 1; i <= 3; i++)
    {
        var ti = t0.AddMilliseconds(100 + i * 80);
        proc.Process(new GsmtcTimelineSample(
            TimeSpan.FromMilliseconds(120000 + i * 10), ti, ti, PlaybackStatus.Playing, 1.0,
            TimelineSampleSource.Poll));
    }

    var later = clock.GetEstimatedPosition().TotalMilliseconds;
    Assert(later >= afterSeek - 50 && later < 130000, "Repeated samples after seek do not re-jump");
}

// ── R: stale TimelineEvent does not reverse seek ──
{
    var clock = new PlaybackClock();
    var proc = new TimelineSampleProcessor(clock);
    var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    proc.Process(new GsmtcTimelineSample(
        TimeSpan.FromMilliseconds(60000), t0, t0, PlaybackStatus.Playing, 1.0, TimelineSampleSource.Poll));
    proc.Process(new GsmtcTimelineSample(
        TimeSpan.FromMilliseconds(120000), t0.AddSeconds(1), t0.AddSeconds(1),
        PlaybackStatus.Playing, 1.0, TimelineSampleSource.Poll));
    var after = clock.GetEstimatedPosition().TotalMilliseconds;
    // Old event
    proc.Process(new GsmtcTimelineSample(
        TimeSpan.FromMilliseconds(60000), t0.AddMilliseconds(100), t0.AddSeconds(2),
        PlaybackStatus.Playing, 1.0, TimelineSampleSource.TimelineEvent));
    Assert(Math.Abs(clock.GetEstimatedPosition().TotalMilliseconds - after) < 100,
        "Stale event does not reverse seek");
}

Console.WriteLine(failed == 0 ? "\nALL PASSED" : $"\n{failed} FAILED");
return failed == 0 ? 0 : 1;
