using System.Text;

namespace InterviewAssistant;

public sealed record InterviewMemoryContext(string Conversation, string SearchQuery);

public static class InterviewMemory
{
    private const int RecentTurnLimit = 5;
    private const int OlderTopicLimit = 8;

    public static InterviewMemoryContext Prepare(IEnumerable<InterviewTurn> turns, Guid sessionId,
        string currentQuestion, Guid? excludedTurnId = null)
    {
        var previous = turns.Where(turn => turn.SessionId == sessionId && turn.Id != excludedTurnId &&
                !turn.Ignored && turn.Completed != false &&
                !string.IsNullOrWhiteSpace(turn.Question) && !string.IsNullOrWhiteSpace(turn.Answer))
            .OrderByDescending(turn => turn.AtUtc).ToList();
        if (previous.Count == 0) return new InterviewMemoryContext("", currentQuestion);

        var result = new StringBuilder();
        var older = previous.Skip(RecentTurnLimit).Take(OlderTopicLimit).Reverse()
            .Select(turn => Clip(turn.Question, 80)).ToList();
        if (older.Count > 0)
        {
            result.AppendLine("更早讨论过的话题（仅保留问题线索，不代表已核实的事实）：");
            foreach (var topic in older) result.Append("• ").AppendLine(topic);
            result.AppendLine();
        }

        result.AppendLine("本场面试最近的问答（用于理解指代和上下文；过往回答不是个人经历的事实依据）：");
        var index = 1;
        foreach (var turn in previous.Take(RecentTurnLimit).Reverse())
        {
            result.Append(index++).Append(". 面试官：").AppendLine(Clip(turn.Question, 180));
            result.Append("   助手之前的回答：").AppendLine(Clip(turn.Answer, 700));
        }

        var searchQuery = currentQuestion + " " + Clip(previous[0].Question, 180);
        return new InterviewMemoryContext(result.ToString().TrimEnd(), searchQuery);
    }

    private static string Clip(string value, int limit)
    {
        var text = value.Trim();
        return text.Length <= limit ? text : text[..(limit - 1)] + "…";
    }
}
