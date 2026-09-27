namespace InterviewAssistant;

public static class SessionHistory
{
    public static bool MigrateLegacyTurns(AppData data)
    {
        var legacy = data.Turns.Where(turn => turn.SessionId == Guid.Empty ||
            data.Sessions.All(session => session.Id != turn.SessionId)).ToList();
        if (legacy.Count == 0) return false;

        var session = new InterviewSession
        {
            Title = "旧版记录（升级前）",
            StartedAtUtc = legacy.Min(turn => turn.AtUtc),
            EndedAtUtc = legacy.Max(turn => turn.AtUtc)
        };
        data.Sessions.Add(session);
        foreach (var turn in legacy) turn.SessionId = session.Id;
        return true;
    }

    public static InterviewSession Start(AppData data, DateTime nowUtc)
    {
        End(data, nowUtc);
        var session = new InterviewSession
        {
            Title = nowUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + " 面试",
            StartedAtUtc = nowUtc
        };
        data.Sessions.Add(session);
        data.ActiveSessionId = session.Id;
        return session;
    }

    public static InterviewSession EnsureActive(AppData data, DateTime nowUtc) =>
        data.Sessions.FirstOrDefault(session => session.Id == data.ActiveSessionId)
        ?? Start(data, nowUtc);

    public static void End(AppData data, DateTime nowUtc)
    {
        var active = data.Sessions.FirstOrDefault(session => session.Id == data.ActiveSessionId);
        if (active is not null && !active.EndedAtUtc.HasValue) active.EndedAtUtc = nowUtc;
        data.ActiveSessionId = null;
    }
}
