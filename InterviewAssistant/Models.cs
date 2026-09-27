using System.Text.Json.Serialization;

namespace InterviewAssistant;

public enum DocumentKind { Resume, JobDescription, Knowledge }

public static class AnswerInstructionDefaults
{
    public const string Text = "如果面试官的问题在我的资料里有现成答案，优先使用资料里的原话回答。尽量保留原有措辞、顺序和具体细节，只做让回答连贯、适合口述的少量调整。\n\n" +
        "资料只有相关经历时，依据真实内容整理成中文、第一人称回答；资料不足时不要编造公司、项目、数字、成绩或技能。岗位 JD 只用于理解岗位要求，不要把岗位要求说成我的经历。\n\n" +
        "遇到投屏截图里的 SQL 题，先读清题目、表结构、字段和限制条件，给出可执行 SQL，再简要解释关键逻辑；条件不清楚时写明假设。";
}

public sealed class AnswerRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Text { get; set; } = "";
    [JsonIgnore] public int Position { get; set; }
    [JsonIgnore] public int TotalCount { get; set; }
    [JsonIgnore] public string Label => $"要求 {Position:00}";
    [JsonIgnore] public bool CanMoveUp => Position > 1;
    [JsonIgnore] public bool CanMoveDown => Position < TotalCount;
}

public sealed class SourceDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DocumentKind Kind { get; set; }
    public string Name { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime ImportedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class InterviewTurn
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
    public string Question { get; set; } = "";
    public string Answer { get; set; } = "";
    public double? FirstTextLatencyMs { get; set; }
    public bool Ignored { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Completed { get; set; }
}

public sealed class InterviewSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAtUtc { get; set; }
}

public sealed class AppData
{
    public List<SourceDocument> Documents { get; set; } = [];
    public List<InterviewTurn> Turns { get; set; } = [];
    public List<InterviewSession> Sessions { get; set; } = [];
    public Guid? ActiveSessionId { get; set; }
    public string? SelectedDeviceId { get; set; }
    public string TencentAppId { get; set; } = "";
    public string TencentSecretId { get; set; } = "";
    public string DeepSeekReasoningEffort { get; set; } = "none";
    public bool UseInterviewMemory { get; set; } = true;
    public List<AnswerRule>? AnswerRules { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AiAnswerInstructions { get; set; }
}
