using System.Text;

namespace InterviewAssistant;

public sealed record InterviewMemoryContext(string Conversation, string SearchQuery);

public static class InterviewMemory
{
    private const int RecentTurnLimit = 5;
    private const int OlderTopicLimit = 8;

    public static InterviewMemoryContext Prepare(IEnumerable<InterviewTurn> turns, Guid sessionId,
        string currentQuestion, Guid? excludedTurnId = null, IEnumerable<InterviewSpeech>? speeches = null)
    {
        var sessionSpeech = (speeches ?? []).Where(note => note.SessionId == sessionId &&
            !string.IsNullOrWhiteSpace(note.Text)).ToList();
        var spokenByTurn = sessionSpeech.Where(note => note.TurnId.HasValue &&
                !string.IsNullOrWhiteSpace(note.Text))
            .GroupBy(note => note.TurnId!.Value)
            .ToDictionary(group => group.Key, group => string.Join(" ", group.OrderBy(note => note.AtUtc).Select(note => note.Text.Trim())));
        var previous = turns.Where(turn => turn.SessionId == sessionId && turn.Id != excludedTurnId &&
                !turn.Ignored && !string.IsNullOrWhiteSpace(turn.Question) &&
                (spokenByTurn.ContainsKey(turn.Id) || (turn.Completed != false && !string.IsNullOrWhiteSpace(turn.Answer))))
            .OrderByDescending(turn => turn.AtUtc).ToList();
        var recentUnlinked = sessionSpeech.Where(note => note.TurnId is null &&
                note.AtUtc >= DateTime.UtcNow.AddMinutes(-10))
            .OrderByDescending(note => note.AtUtc).Take(3).Reverse().ToList();
        if (previous.Count == 0 && recentUnlinked.Count == 0)
            return new InterviewMemoryContext("", currentQuestion);

        var result = new StringBuilder();
        var older = previous.Skip(RecentTurnLimit).Take(OlderTopicLimit).Reverse()
            .Select(turn => Clip(turn.Question, 80)).ToList();
        if (older.Count > 0)
        {
            result.AppendLine("更早讨论过的话题（仅保留问题线索，不代表已核实的事实）：");
            foreach (var topic in older) result.Append("• ").AppendLine(topic);
            result.AppendLine();
        }

        if (previous.Count > 0)
        {
            result.AppendLine("本场面试最近的问答（实际口述优先；转写可能有误，个人经历以资料为准）：");
            var index = 1;
            foreach (var turn in previous.Take(RecentTurnLimit).Reverse())
            {
                result.Append(index++).Append(". 面试官：").AppendLine(Clip(turn.Question, 180));
                if (spokenByTurn.TryGetValue(turn.Id, out var spoken))
                    result.Append("   候选人实际口述（语音转写）：").AppendLine(Clip(spoken, 700));
                else result.Append("   助手之前的回答（未必实际说出）：").AppendLine(Clip(turn.Answer, 700));
            }
        }

        if (recentUnlinked.Count > 0)
        {
            result.AppendLine("最近未关联具体问题的候选人口述（语音转写，可能有误）：");
            foreach (var note in recentUnlinked)
                result.Append("• ").AppendLine(Clip(note.Text, 180));
        }

        var searchQuery = previous.Count == 0 ? currentQuestion : currentQuestion + " " + Clip(previous[0].Question, 180);
        return new InterviewMemoryContext(result.ToString().TrimEnd(), searchQuery);
    }

    private static string Clip(string value, int limit)
    {
        var text = value.Trim();
        return text.Length <= limit ? text : text[..(limit - 1)] + "…";
    }
}
