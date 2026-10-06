namespace TaskbarLyrics.App.Config;
public enum SavePhase { Pending, Saving, Saved, Failed }
public sealed record ConfigSaveState(long Revision, SavePhase Phase, string? Error = null);
