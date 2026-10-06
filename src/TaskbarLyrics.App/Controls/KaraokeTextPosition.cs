namespace TaskbarLyrics.App.Controls;

/// <summary>Horizontal placement in a finite, positive lyric viewport.</summary>
public static class KaraokeTextPosition
{
    public static double ComputeOriginX(double availableWidth, double textWidth, string alignment, double panOffsetX)
    {
        // Overflow always uses the existing pan position, irrespective of short-line alignment.
        if (textWidth > availableWidth) return -panOffsetX;
        var remaining = availableWidth - textWidth;
        return alignment switch
        {
            "Left" => 0,
            "Right" => remaining,
            _ => remaining / 2.0
        };
    }
}
