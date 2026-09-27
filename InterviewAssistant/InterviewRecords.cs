namespace InterviewAssistant;

public sealed class InterviewRecordItem
{
    public required InterviewTurn Turn { get; init; }
    public string Time => Turn.AtUtc.ToLocalTime().ToString("HH:mm:ss");
    public string Question => Turn.Question;
    public required string SpokenAnswer { get; init; }
    public string DraftStatus => Turn.Ignored ? "已忽略" :
        string.IsNullOrWhiteSpace(Turn.Answer) ? "尚未生成 AI 草稿" : "已生成 AI 草稿";
}

public static class InterviewRecords
{
    public static List<InterviewRecordItem> ForSession(AppData data, Guid sessionId)
    {
        var spokenByTurn = data.Speeches.Where(note => note.SessionId == sessionId && note.TurnId.HasValue)
            .GroupBy(note => note.TurnId!.Value)
            .ToDictionary(group => group.Key, group => string.Join("\n", group.OrderBy(note => note.AtUtc)
                .Select(note => note.Text.Trim()).Where(text => text.Length > 0)));
        return data.Turns.Where(turn => turn.SessionId == sessionId)
            .OrderBy(turn => turn.AtUtc)
            .Select(turn => new InterviewRecordItem
            {
                Turn = turn,
                SpokenAnswer = spokenByTurn.TryGetValue(turn.Id, out var spoken) && spoken.Length > 0
                    ? spoken : "尚未录到我的回答"
            }).ToList();
    }

    public static List<InterviewSpeech> Unlinked(AppData data, Guid sessionId)
    {
        var turnIds = data.Turns.Where(turn => turn.SessionId == sessionId).Select(turn => turn.Id).ToHashSet();
        return data.Speeches.Where(note => note.SessionId == sessionId &&
                (!note.TurnId.HasValue || !turnIds.Contains(note.TurnId.Value)))
            .OrderBy(note => note.AtUtc).ToList();
    }
}
