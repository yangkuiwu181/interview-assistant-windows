# 面试助手开发说明

本文件适用于整个仓库。用户在当前对话中的要求优先于这里的约定。

## 项目结构

- `InterviewAssistant/`：.NET 10 WPF Windows 桌面应用。
- `MainWindow.xaml`、`MainWindow.xaml.cs`：悬浮窗界面与交互。
- `AudioCapture.cs`、`TencentAsr.cs`：播放设备、麦克风采集与腾讯云实时转写。
- `DeepSeekClient.cs`、`DocumentService.cs`、`InterviewMemory.cs`：回答生成、资料检索与本场上下文。
- `InterviewRecords.cs`、`SessionHistory.cs`、`Models.cs`：问题、实际口述和面试场次。
- `Storage.cs`：本地数据及 Windows DPAPI 密钥存储。
- `SmokeTests/`：离线功能检查；`README.md`：使用与构建说明。

## 开发与验证

- 使用 .NET 10 SDK 在 Windows 上构建：`dotnet build InterviewAssistant/InterviewAssistant.csproj -c Release`。
- 运行离线检查：`dotnet run --project SmokeTests/SmokeTests.csproj -c Release`。
- 涉及音频设备时，可按需运行 `dotnet run --project SmokeTests/SmokeTests.csproj -c Release -- --audio` 或 `-- --microphone`。真实云端识别和回答会产生服务用量；离线检查不应依赖 API 密钥。
- 发布 Windows x64 自包含版本：`dotnet publish InterviewAssistant/InterviewAssistant.csproj -c Release -r win-x64 --self-contained true -o dist/InterviewAssistant`。运行发布版时保留同目录全部依赖文件。
- 修改界面时检查小窗口和滚动状态；修改记录逻辑时验证多场次隔离、问题与口述关联、未关联口述保留，以及重启后的持久化。

## 数据与兼容性

- 用户资料、场次和问答位于 `%USERPROFILE%\.interview-assistant\data.json`；API 密钥保存在同目录的 DPAPI 加密文件。`dist/` 和本地数据均不属于源代码。
- 不在日志、测试输出、截图、提交或发布包中暴露简历原文、面试记录、API 密钥或签名后的腾讯云 WebSocket 地址。不要保存原始音频。
- 修改持久化模型或切换本机运行版本时，保留旧数据，核对文档、场次、问题、口述及密钥；不要用测试数据覆盖用户数据。测试使用独立的临时存储目录。
- 问题由确定转写保存；用户实际口述与 AI 草稿是不同数据。展示和连续记忆时保留这一区别，未关联问题的口述也要可查看、可校正。

## 交付

- 修改功能时同步更新 `README.md` 中受影响的使用说明。
- 提交前检查 `git diff --check`、构建结果和相关离线检查。发布包只包含程序及依赖，不包含本机数据。
- 本机有旧版正在运行时，先构建到独立目录并验证，再切换运行程序或桌面快捷方式。
