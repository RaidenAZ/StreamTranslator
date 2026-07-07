# StreamTranslator Docs

建议按以下顺序阅读：

1. [architecture.md](architecture.md)
   了解系统架构、音频链路、VAD、ASR worker、UI、数据目录和运行时生命周期。

2. [decisions.md](decisions.md)
   查看已经确认的关键技术和产品决策。实现时默认以这些决策为准。

3. [v1.0-plan.md](v1.0-plan.md)
   查看 V1.0 范围、Definition of Done、测试策略、诊断输出和后续版本规划。

当前核心结论：

- V1.0 只做 ASR 实时字幕，不做翻译。
- C# 负责音频捕获、格式转换、VAD、分段和 UI。
- Python worker 只负责调用 MiMo ASR API。
- C# 与 Python 使用 stdin/stdout JSON 协议通信。
- VAD 使用 C# 侧 Silero ONNX，默认尾静音 300ms，可配置。
- 必须有悬浮字幕窗，主窗口保留设置和状态页面。
- 发布形态是 Windows 10/11 x64 绿色便携版。

