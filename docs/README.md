# StreamTranslator Docs

建议按以下顺序阅读：

1. [architecture.md](architecture.md)
   了解系统架构、音频链路、VAD、ASR worker、UI、数据目录和运行时生命周期。

2. [decisions.md](decisions.md)
   查看已经确认的关键技术和产品决策。实现时默认以这些决策为准。

3. [v1.0-plan.md](v1.0-plan.md)
   查看 V1.0 范围、Definition of Done、测试策略、诊断输出和后续版本规划。

4. [v1.1-adaptive-vad.md](v1.1-adaptive-vad.md)
   查看已经完成 grilling 并进入实现阶段的自适应尾静音、字幕分组、历史修订和验收规格。

当前核心结论：

- V1.0 只做 ASR 实时字幕，不做翻译。
- C# 负责音频捕获、格式转换、VAD、分段和 UI。
- Python worker 只负责调用 MiMo ASR API。
- C# 与 Python 使用 stdin/stdout JSON 协议通信。
- V1.0 VAD 使用 C# 侧 Silero ONNX 和可配置固定尾静音；V1.1 将默认升级为 `低延迟 / 均衡 / 句子完整` 三档单变量自适应端点，并保留固定值模式。
- 音频使用有状态 WDL 重采样，无播放数据时补静音帧以完成尾静音判断。
- Python worker 以默认并发 2 调用 MiMo，并返回结构化错误供 C# 重试或停机。
- 必须有悬浮字幕窗，主窗口保留设置和状态页面。
- 发布形态是 Windows 10/11 x64 绿色便携版。
- V1.1 自适应只读取 VAD 时间信号，不读取音频内容、ASR 文本、LLM 语义或直播类别。
- 疑似误切只在字幕/UI/历史层修订，不拼接 WAV，也不增加 MiMo 请求。
