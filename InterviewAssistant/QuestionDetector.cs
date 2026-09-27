namespace InterviewAssistant;

public static class QuestionDetector
{
    private static readonly string[] Cues =
    [
        "?", "？", "介绍", "说说", "谈谈", "聊聊", "聊一下", "讲讲", "描述", "分享", "解释",
        "为什么", "怎么", "如何", "什么", "哪些", "是否", "能否", "请你", "你的", "你们", "有没有", "举个例"
    ];

    public static bool LooksLikePrompt(string text) =>
        text.Trim().Length >= 4 && Cues.Any(text.Contains);
}
