namespace TaskbarLyrics.App.Presentation;
public static class LineTransitionPolicy
{
    public static bool ShouldAnimate(long oldGeneration, long generation, int previousLine, int line, long previousPositionMs, long positionMs, bool scrubbing, bool staticLine, bool reduced) =>
        !reduced && !scrubbing && !staticLine && oldGeneration == generation && previousLine >= 0 && line == previousLine + 1 && positionMs >= previousPositionMs && positionMs - previousPositionMs < 250;
}
