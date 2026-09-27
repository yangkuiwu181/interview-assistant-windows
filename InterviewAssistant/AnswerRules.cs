using System.Text.RegularExpressions;

namespace InterviewAssistant;

public static class AnswerRules
{
    public static bool Migrate(AppData data)
    {
        if (data.AnswerRules is not null) return false;
        data.AnswerRules = Split(data.AiAnswerInstructions ?? AnswerInstructionDefaults.Text);
        data.AiAnswerInstructions = null;
        return true;
    }

    public static List<AnswerRule> Defaults() => Split(AnswerInstructionDefaults.Text);

    public static List<AnswerRule> Split(string text) =>
        Regex.Split(text.Trim(), @"\r?\n\s*\r?\n")
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .Select(part => new AnswerRule { Text = part })
            .ToList();

    public static string Compose(IEnumerable<AnswerRule> rules) =>
        string.Join("\n", rules.Select(rule => rule.Text.Trim())
            .Where(value => value.Length > 0)
            .Select((value, index) => $"{index + 1}. {value}"));
}
