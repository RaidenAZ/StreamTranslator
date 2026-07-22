# StreamTranslator Docs

建议按以下顺序阅读：

1. [architecture.md](architecture.md)
   了解系统架构、音频链路、VAD、ASR worker、UI、数据目录和运行时生命周期。

2. [decisions.md](decisions.md)
   查看已经确认的关键技术和产品决策。实现时默认以这些决策为准。

3. [v1.0-plan.md](v1.0-plan.md)
   查看 V1.0 范围、Definition of Done、测试策略、诊断输出和后续版本规划。

4. [v1.1-adaptive-vad.md](v1.1-adaptive-vad.md)
   查看自适应尾静音、字幕分组、历史修订和验收规格。

5. [v1.2-translation.md](v1.2-translation.md)
   查看 V1.2 翻译字幕的产品行为、配置、队列、UI、历史、诊断和实施顺序。

6. [v1.2-translation-protocol.md](v1.2-translation-protocol.md)
   查看独立 translation worker、JSONL 消息、Prompt、Chat Completions 和错误契约。

7. [v1.2-translation-test-plan.md](v1.2-translation-test-plan.md)
   查看自动测试、DeepSeek 实测、UI smoke、质量门槛和发布验收清单。

8. [v1.0-v1.1-release-backlog.md](v1.0-v1.1-release-backlog.md)
   查看 V1.0/V1.1 已实现功能的发布收口、手工验收、真实直播样本验收和明确延期项。

当前核心结论：

- V1.0/V1.1 只做 ASR 实时字幕；V1.2 增加默认关闭的双语翻译。
- C# 负责音频捕获、格式转换、VAD、分段和 UI。
- ASR worker 只负责调用 MiMo ASR API；V1.2 translation worker 只负责文本翻译协议。
- C# 与两个 Python workers 都使用 stdin/stdout JSON 协议通信。
- V1.0 VAD 使用 C# 侧 Silero ONNX 和可配置固定尾静音；V1.1 默认使用 `低延迟 / 均衡 / 句子完整` 三档单变量自适应端点，并保留固定值模式。
- 音频使用有状态 WDL 重采样，无播放数据时补静音帧以完成尾静音判断。
- Python worker 以默认并发 2 调用 MiMo，并返回结构化错误供 C# 重试或停机。
- 必须有悬浮字幕窗，主窗口保留设置和状态页面。
- 发布形态是 Windows 10/11 x64 绿色便携版。
- V1.1 自适应只读取 VAD 时间信号，不读取音频内容、ASR 文本、LLM 语义或直播类别。
- 疑似误切只在字幕/UI/历史层修订，不拼接 WAV，也不增加 MiMo 请求。
- V1.2 使用独立 translation worker 和 OpenAI Chat Completions compatible 协议，翻译故障不影响原文字幕。
- V1.2 原文立即显示，完整译文随后一次性补充；rolling interim ASR 移到 V1.3。
- V1.2 代码、便携包和默认 fake 评估已落地；真实 DeepSeek 人工质量验收需显式提供评估专用 Key 后执行。
