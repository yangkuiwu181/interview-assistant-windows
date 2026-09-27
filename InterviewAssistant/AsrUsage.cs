namespace InterviewAssistant;

public static class AsrUsage
{
    public const int FramesPerSecond = 5;

    public static int NewFrames(int sentFrames, ref int countedFrames)
    {
        // A late progress callback from a previous connection must not count twice.
        if (sentFrames <= countedFrames) return 0;
        var added = Math.Max(0, sentFrames - countedFrames);
        countedFrames = sentFrames;
        return added;
    }

    public static double TotalSeconds(InterviewSession session) =>
        (session.PlaybackAsrFrames + session.MicrophoneAsrFrames) / (double)FramesPerSecond;

    public static bool LimitReached(InterviewSession session, int limitMinutes) =>
        limitMinutes > 0 && TotalSeconds(session) >= limitMinutes * 60;

    public static bool NearLimit(InterviewSession session, int limitMinutes) =>
        limitMinutes > 0 && TotalSeconds(session) >= limitMinutes * 60 * 0.8;

    public static string FormatFrames(int frames) =>
        TimeSpan.FromSeconds(frames / (double)FramesPerSecond).ToString(@"hh\:mm\:ss");
}
