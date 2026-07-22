# StreamTranslator Decisions

本文档记录已确认的 V1.0 和 V1.1 架构与产品决策。每条决策都应作为后续实现时的默认依据，除非有新的明确变更。

## D001: C# / Python Boundary Uses Bytes, Not Temporary Files

状态：Accepted

C# 不把临时 WAV 文件作为热路径传给 Python。C# 完成音频捕获、格式转换、VAD、分段和 WAV bytes 生成，然后通过 stdin/stdout JSON 协议把 Base64 音频传给 Python worker。

理由：

- 减少磁盘 IO、临时文件清理、并发命名和异常残留问题。
- ASR API 本质需要音频内容，不需要本地文件路径。
- 便于后续队列、重试、取消和诊断。

## D002: VAD Runs In C#

状态：Accepted

VAD 和分段全部放在 C# 侧。Python 不处理实时音频状态，只做 ASR API 调用适配。

理由：

- C# 已经持有原始捕获流。
- Python worker 职责更单一。
- 避免在 Python 侧引入 PyTorch 等复杂依赖。

## D003: V1.0 Uses Silero VAD ONNX

状态：Accepted

V1.0 以准确度优先，C# 侧挂载 Silero VAD ONNX 模型，并通过 Microsoft.ML.OnnxRuntime 推理。

正式运行缺少 Silero 模型时直接阻止启动。WebRTC VAD 和能量阈值 VAD 只能作为后续显式诊断选项，不能静默降级。

理由：

- 比纯能量阈值更适合直播音频。
- 不需要安装 PyTorch。
- ONNX 模型适合随应用打包。

参考：

- https://github.com/snakers4/silero-vad
- https://onnxruntime.ai/docs/get-started/with-csharp.html

## D004: Audio Internal Format Is 16kHz Mono PCM16 WAV

状态：Accepted

所有音频进入 VAD 和 ASR 前统一转为：

```text
16kHz / mono / PCM16
```

理由：

- 简化 VAD、分段、WAV 编码和测试。
- 降低传输数据量。
- 对语音识别足够。

## D005: VAD End Silence Is User Configurable

状态：Accepted for V1.0，V1.1 由 D027 扩展

默认尾静音判定为 300ms。用户可在设置页配置。

建议范围：

```text
200-800ms
step: 50ms
default: 300ms
```

UI 文案：

```text
断句等待
声音停止后等待多久生成字幕
```

## D006: Long Speech Uses Soft/Hard Max With Overlap

状态：Accepted

长句不能无限等待。V1.0 使用：

```text
soft max: 约 4s
hard max: 约 10s
overlap: 约 600ms
```

soft max 后优先寻找自然断点；hard max 后强制切片。强切时下一段带上一段末尾 overlap 音频。

## D007: V1.0 Does Not Use LLM For Text Deduplication

状态：Accepted

V1.0 只做本地确定性 suffix/prefix 去重。

不做：

- 调用 LLM 合并字幕。
- 依赖 ASR API 上下文合并能力。
- 语义级改写。
- 润色或纠错。

理由：

- 低延迟链路不应引入额外 LLM 请求。
- LLM 可能改变字幕原文。
- 本地确定性逻辑更可测、更可控。

## D008: Python Worker Uses stdin/stdout JSON Protocol

状态：Accepted

C# 启动长驻 Python worker 子进程，使用 stdin/stdout JSON Lines 通信。

不使用：

- 本地 HTTP 服务作为 V1.0 默认方案。
- named pipe 作为 V1.0 默认方案。
- 临时文件作为热路径。

理由：

- 不占端口。
- 生命周期与 C# 进程绑定。
- 依赖少，调试直接。

## D009: Python Worker Is Managed By C#

状态：Accepted

Python worker 是实现细节，不暴露给用户手动管理。

行为：

- 用户打开 C# UI 时，不立即启动 worker。
- 用户点击开始字幕时启动 worker。
- 用户点击停止字幕时关闭 worker。
- 用户关闭主窗口时退出整个程序并清理 worker。

若 worker 在 2-3 秒内无法正常退出，C# 使用 `Kill(entireProcessTree: true)` 清理。

## D010: Close Main Window Means Exit App

状态：Accepted

V1.0 关闭主窗口就是退出整个程序。

V1.0 不做托盘常驻。

理由：

- 避免用户误以为程序已关闭但仍在捕获音频或调用 API。
- 生命周期更清晰。

## D011: ASR Concurrency Is Limited

状态：Accepted

默认 ASR 并发数为 2。C# 侧维护队列和结果重排序。

Python worker 使用同样上限的有界线程池并行调用 MiMo，stdout 使用写锁输出 JSON Lines。

策略：

- 通过 `sequence` 保持时间线顺序。
- 慢段不能永久阻塞后续段。
- API 限流时当前段重试一次，仍失败则标记失败并提示；是否动态降低并发留到后续版本根据诊断数据决定。

## D012: V1.0 Does Not Default To Rolling Interim ASR

状态：Accepted for V1.0，后续版本顺序由 D027 调整

V1.0 只发 final / final-ish 音频片段。数据模型和 UI 预留 interim 替换能力。

滚动 interim ASR 放到：

```text
V1.2: 实验功能，默认关闭
后续版本: 若稳定，再考虑正式 rolling interim 模式
```

理由：

- interim 会显著增加 API 调用次数、成本、限流和合并复杂度。
- V1.0 先稳定主链路。

## D013: UI Must Include Floating Subtitle Window

状态：Accepted

V1.0 必须有悬浮字幕窗，同时保留主窗口设置页。

悬浮窗默认编辑模式，不点击穿透。用户锁定后启用置顶和点击穿透。

必备能力：

- Always on top
- 拖动
- 缩放宽度
- 字号设置
- 背景透明度设置
- 显示行数 1-3
- 锁定/解锁
- 点击穿透
- 快速隐藏/显示

## D014: Main Window Uses Workbench Layout

状态：Accepted

主窗口使用左侧导航 + 右侧页面，不做营销首页。

页面：

- 状态
- 字幕
- 音频
- 服务
- 悬浮窗
- 关于

## D015: Global Hotkeys Are Included

状态：Accepted

V1.0 内置 3 个全局快捷键，并允许用户关闭。

```text
Ctrl+Alt+S: 开始/停止字幕
Ctrl+Alt+H: 隐藏/显示悬浮窗
Ctrl+Alt+L: 锁定/解锁悬浮窗
```

实现建议：Windows `RegisterHotKey`。

## D016: Capture Whole Output Device Only

状态：Accepted

V1.0 只捕获指定输出设备的整体播放声音，例如系统扬声器、耳机。

不做按应用、窗口、浏览器标签页捕获。

理由：

- WASAPI loopback 成熟稳定。
- 按进程音频捕获复杂度和兼容性风险更高。

## D017: Portable Distribution

状态：Accepted

V1.0 按绿色便携版设计。

发布版目录示例：

```text
StreamTranslator/
  StreamTranslator.exe
  worker/
    asr_worker.exe
  models/
    silero_vad.onnx
  data/
```

发布版内置 Python worker exe，不要求用户安装 Python。

## D018: Data Lives Next To The Exe

状态：Accepted

数据优先写启动 exe 同目录下的 `data/`。

如果不可写，提示错误，不静默 fallback 到 `%LocalAppData%`。

## D019: Settings And API Key Are Plaintext

状态：Accepted

所有配置包括 API Key 都明文存放在：

```text
data/settings.json
```

风险约束：

- UI 中 API Key 仍使用 PasswordBox。
- 日志不得输出完整 API Key。
- 诊断信息不得包含 API Key。
- 若未来启用 git，应忽略 `data/`。

## D020: History Is Saved, Audio Is Not Saved By Default

状态：Accepted

V1.0 默认保存字幕历史，不保存音频片段。

字幕历史：

```text
data/subtitles/YYYY-MM-DD.jsonl
```

音频片段仅在诊断模式开启后保存。

## D021: No Subtitle File Export In V1.0

状态：Accepted

V1.0 不支持导出 TXT、SRT 或其他字幕文件。

但历史页支持：

- 复制选中字幕
- 复制最近 N 条
- 复制当天全部文本
- 清空当天历史

## D022: Diagnostics Are Built In But Off By Default

状态：Accepted

V1.0 内置诊断模式，默认关闭。

开启后保存：

- segment WAV
- VAD probability timeline
- session JSON
- ASR latency
- dedup result

## D023: Offline Audio Segmentation CLI Is Included

状态：Accepted

V1.0 增加离线音频回放/分段 CLI 诊断工具。

目标：

- 用固定音频样本复现 VAD/segment 行为。
- 支撑回归测试和后续调参。
- 不塞进普通用户主流程。

## D024: Error Handling Retries Once

状态：Accepted

V1.0 对明确可恢复的错误自动重试一次，不做无限重试。

worker 返回结构化错误分类。401/403 不重试并停止字幕；429、timeout、network、5xx 仅重试一次；每个 sequence 无论成功失败都必须释放重排缓冲。

错误状态分层显示：

- 音频捕获
- VAD
- ASR worker
- ASR API

## D025: Tests Are Part Of V1.0

状态：Accepted

V1.0 一次性建立完整测试与诊断体系。

重点：

- SpeechSegmenter 单元测试
- TextDeduplicator 单元测试
- Settings 读写测试
- Python worker 协议测试
- Python fake ASR 集成测试
- 音频样本回归测试
- 手工验收清单

## D026: V1.0 Project Structure

状态：Accepted

项目结构：

```text
StreamTranslator/
  src/
    StreamTranslator.App/
    StreamTranslator.Core/
    StreamTranslator.Audio/
    StreamTranslator.Diagnostics/
  python/
    asr_worker.py
    requirements.txt
    tests/
  models/
    silero_vad.onnx
  tests/
    StreamTranslator.Core.Tests/
    StreamTranslator.Audio.Tests/
  data/
  docs/
```

## D027: V1.1 Uses Goal-Based Adaptive End Silence

状态：Accepted

V1.1 不按体育、游戏、新闻或发布会等直播类别选择 VAD 参数。用户选择优化目标：

| 模式 | 初始值 | 范围 |
|---|---:|---:|
| 低延迟 | 250ms | 200-400ms |
| 均衡 | 400ms | 280-600ms |
| 句子完整 | 600ms | 400-800ms |
| 固定值 | 用户输入 | 200-800ms |

第一版只动态调整 `EndSilenceMs`，其他 VAD 参数保持不变。自适应逻辑位于 C# `AdaptiveEndpointController`，只读取 VAD 决策和时间信号，不读取 ASR 文本、LLM 语义或直播类别。

固定值模式继续测量 quick-resume metrics，但不学习、不调整、不触发字幕合并。

## D028: Adaptive Learning Uses A Short Event Window

状态：Accepted

控制器使用最近 8 次有效停顿，样本最长保留 15 秒，至少 3 个样本后开始调整。目标参考 P75 加 50ms 安全余量。

调整策略：

- 连续两次疑似误切后上调 50ms。
- 至少 6 个稳定样本后才允许下调，每次 25ms。
- 调整冷却 2 秒，每 10 秒最多调整 2 次。
- 10 秒没有确认语音时清空样本，并逐步回到模式初始值。

学习状态只在当前捕获会话内有效，不跨会话保存。

## D029: Quick Resume Produces Subtitle-Layer Revisions

状态：Accepted

因尾静音切段后，完整停顿不超过 800ms 时视为疑似过早切段。完整停顿等于切段时端点加切段后到确认语音恢复的时间。

处理方式：

- 两个音频片段仍分别调用 ASR。
- 不拼接 WAV，不增加第三次 MiMo 请求。
- 相邻结果在字幕层合并并静默替换 UI。
- 支持最多 3 段、最长 12 秒的有限链式合并。
- 只能合并相邻 sequence；ASR 失败或任何边界超限时关闭分组。
- 使用本地确定性 suffix/prefix 去重，不调用 LLM。

## D030: Subtitle History Uses Append-Only Revision Events

状态：Accepted

字幕合并不原地改写 `data/subtitles/YYYY-MM-DD.jsonl`。历史追加带稳定 `utteranceGroupId`、revision 和 `replacesSequences` 的修订事件，读取时只物化每组最新 revision。

普通 UI 不显示“修订中”；修订次数、原始 sequence 和合并原因只进入诊断和历史事件。

## D031: V1.1 Forces Balanced Adaptive Mode

状态：Accepted

当前仍处于测试阶段，V1.1 对新安装和旧配置都默认强制使用 `均衡` 自适应模式。不猜测旧数字对应的优化目标，不提供旧值恢复入口或阻塞确认弹窗。

用户需要固定端点时可主动选择 `固定值` 并填写 200-800ms。

## D032: Adaptive VAD Has Quantitative Acceptance Gates

状态：Accepted

同一批直播样本相对固定 300ms 基线必须满足：

- 疑似过早切段率至少下降 30%。
- 均衡模式端点中位数不超过 450ms，P95 不超过 600ms。
- 每 10 秒最多调整 2 次。
- 错误合并率低于 5%。
- 合并最多 3 段、最长 12 秒。
- ASR 调用次数不因字幕合并增加。
- 固定值模式与 V1.0 行为等价。

完整实现规格见 [v1.1-adaptive-vad.md](v1.1-adaptive-vad.md)。

## D033: V1.2 Uses A Separate Translation Worker

状态：Accepted

V1.2 新增独立 `translation_worker.exe`，不与 `asr_worker.exe` 合并。

- ASR worker 只负责 MiMo ASR。
- translation worker 只负责文本翻译请求、Prompt 构建、响应规范化和错误分类。
- C# 分别管理两个进程，并分别显示 worker 与服务状态。
- 翻译故障、熔断或 worker crash 不得停止音频捕获和原文字幕。
- translation worker 跟随捕获会话启动和停止，本地模型服务由用户自行管理。

## D034: Translation Uses OpenAI Chat Completions Only

状态：Accepted

V1.2 只支持 OpenAI Chat Completions compatible 协议，不支持 Anthropic Messages。

用户可以保存多个命名模型配置，同时只激活一个。远程 API 和本地 vLLM、Ollama、llama.cpp、LM Studio 等服务使用同一协议适配器。模型名手动填写，不调用 `/models`。

Base URL 不自动添加 `/v1`。UI 实时预览最终 `/chat/completions` 地址。远程配置强制 HTTPS，本地配置允许 HTTP。

请求兼容模板：

- Standard：空 `extraBody`。
- DeepSeek：`thinking.type=disabled`。
- Qwen + vLLM：`chat_template_kwargs.enable_thinking=false`。
- Custom：受保留字段和大小限制的 JSON object。

## D035: Translation Is Immediate, Non-Streaming And Revision-Aware

状态：Accepted

ASR 原文立即显示，完整译文使用非流式请求并在返回后一次性补充，不压住原文等待双语同时出现。

subtitle revision 后立即清除旧译文，对最新合并原文重新翻译。旧请求返回后按 `utteranceGroupId + sourceRevision` 判为 stale 并丢弃。译文乱序返回时独立填充自己的字幕组，不等待前序译文。

## D036: Translation Uses Bounded Context And Skips Known Same-Language Text

状态：Accepted

每次翻译最多附带最近 3 组、30 秒内的稳定原文和可用译文。上下文只用于消歧，不允许模型重新输出上下文。

源语言继承 ASR 设置。明确 `zh -> zh-Hans` 或 `en -> en` 时不调用翻译模型。ASR 为 `auto` 时使用保守的中英文字符判断；中英混合、短专有名词和低置信文本继续请求模型。

该策略必须以错误隐藏有效译文次数为 0 进行专项验收。

## D037: Translation Profiles Are Session-Immutable

状态：Accepted

翻译默认关闭，目标语言默认简体中文。用户可以保存多个配置，但活动配置、目标语言、翻译开关、并发、超时和兼容模板在捕获会话开始后全部锁定。

运行中不允许开启、关闭或切换翻译配置。用户必须停止字幕后修改。V1.2 不自动故障转移，不自动管理本地模型服务，也不自动补翻历史。

## D038: Translation Prioritizes Realtime Backpressure

状态：Accepted

默认翻译并发 2，可配置 1-4；请求超时默认 10 秒，可配置 3-30 秒。待处理队列固定 8 条，任务排队超过 10 秒允许丢弃旧译文，原文始终保留。

网络、超时、429 和 5xx 最多重试一次。连续 3 条 transient failure 触发 10-60 秒会话级熔断。translation worker 每个会话最多自动重启一次。停止字幕最多等待 3 秒排空译文。

队列长度和等待时限是否开放给用户留到真实 metrics 后再评估。

## D039: V1.2 Uses Bilingual Overlay And Append-Only Translation History

状态：Accepted

悬浮窗和历史固定原文在上、译文在下。原文使用用户字号，译文默认 90%。原文和译文都自动换行，不使用横向滚动。

悬浮窗字号在 V1.2 配置迁移中强制改为 18。“最大行数”改为“显示字幕数”，默认 2，范围 1-3。

成功译文追加 `translation_result`，不得原地改写原始字幕事件。读取时只物化与当前 source revision 匹配的最新成功译文。历史和复制内容不显示目标语言标签。

## D040: V1.2 Uses A Fixed Faithful Translation Prompt

状态：Accepted

V1.2 使用内置、版本化 Prompt，不允许用户编辑，也不提供术语表。

- 只翻译当前字幕。
- 不总结、解释、扩写、审查或猜测缺失内容。
- 保留数字、时间、单位、专有名词、口语和语气。
- 字幕和上下文必须视为不可信数据，不能执行其中的指令。
- 只返回纯文本译文。

默认不发送 temperature、top-p、top-k 等采样参数。应用不做跨字幕翻译缓存，也不批量翻译多条字幕。

## D041: V1.2 Has Translation-Specific Acceptance Gates

状态：Accepted

默认自动测试使用 fake OpenAI-compatible server，不调用真实 API。发布前显式执行 DeepSeek V4 Flash 远程验收，实际模型标识手动填写，不硬编码到应用。

- 中译英和英译中各至少 50 条人工标注。
- 严重错误率低于 5%。
- 数字、时间和单位错误率低于 2%。
- 多余解释、Markdown、思考文本或空译文低于 1%。
- stale revision 误覆盖为 0。
- same-language false skip 为 0。
- DeepSeek 首轮延迟目标为 P50 1.5 秒、P95 3 秒，测试后允许基于数据细化。
- 德语、法语和日语保留为可选目标语言，但不重复执行真实 API 验收；V1.2 真实质量验收只覆盖中文和英语。

本地模型由用户手工测试，不阻塞 V1.2 发布。真实 API 测试默认关闭并要求显式开关。

## D042: V1.2 Is Translation Only

状态：Accepted

V1.2 只实现翻译字幕，不同时实现 rolling interim ASR。rolling interim ASR 移到 V1.3 重新 grilling，避免同时引入 ASR interim、translation interim 和两层 revision。

完整规格见：

- [v1.2-translation.md](v1.2-translation.md)
- [v1.2-translation-protocol.md](v1.2-translation-protocol.md)
- [v1.2-translation-test-plan.md](v1.2-translation-test-plan.md)

## D043: Live Translation Evaluation Keys Use A Process Environment Variable

状态：Accepted

`scripts/translation-evaluate.ps1` 默认只运行无费用 fake 评估。真实 API 评估必须显式传入 `-AllowLiveApi`，并从当前 PowerShell 会话的 `STREAMTRANSLATOR_TRANSLATION_API_KEY` 环境变量读取评估专用 Key。

- 不提供 API Key 命令行参数，避免进入 shell history 和 process arguments。
- 不从样本、请求记录或结果文件读取或写入 Key。
- Base URL、模型名和兼容模板从现有 `data/settings.json` profile 读取。
- 环境变量仅继承到本次评估 worker 进程，不改变应用配置。
