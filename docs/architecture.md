# StreamTranslator Architecture

本文档描述 StreamTranslator V1.0 已交付主链路、V1.1 自适应 VAD 和 V1.2 翻译字幕目标架构。目标是让新参与者快速理解实时字幕链路、模块边界、运行时生命周期和关键数据流。

## Product Goal

StreamTranslator 是面向 Windows 直播观看场景的实时字幕软件。

V1.0/V1.1 只做 ASR 实时字幕。V1.2 在保持原文链路独立可用的前提下增加可选双语翻译，默认关闭。

核心体验目标：

- 捕获指定输出设备的整体播放声音，例如系统扬声器、耳机。
- 将音频实时切分为适合 ASR 的片段。
- 调用 MiMo ASR API 识别文本。
- 在悬浮字幕窗中快速显示字幕。
- 可选调用 OpenAI Chat Completions compatible 模型生成译文。
- 保留主窗口设置页、状态页、字幕历史和诊断能力。

## Platform

V1.0 仅支持：

- Windows 10 x64
- Windows 11 x64

V1.0 不支持：

- Windows 7/8
- x86
- ARM64
- macOS/Linux

## Technology Stack

主程序：

- C#
- WPF
- WPF-UI
- Fluent Design 风格

音频和 VAD：

- WASAPI loopback 捕获输出设备播放音频
- NAudio 或等价音频库处理设备捕获、格式转换和重采样
- Silero VAD ONNX 模型
- Microsoft.ML.OnnxRuntime

ASR worker：

- Python
- `from openai import OpenAI`
- MiMo ASR API

Translation worker：

- Python
- OpenAI Chat Completions compatible API
- 远程 API 或用户自行管理的本地推理服务
- 独立 `translation_worker.exe`

交付：

- 绿色便携版
- 发布版内置打包后的 Python worker exe
- 用户不需要安装 Python

源码调试时如果未打包 worker exe，可通过 `STREAMTRANSLATOR_PYTHON` 指向本机 Python，例如 Python 3.14。

## High-Level Data Flow

```text
WPF App
  -> WASAPI loopback capture
  -> AudioNormalizer: 16kHz / mono / PCM16
  -> SileroOnnxVadEngine
  -> AdaptiveEndpointController (V1.1)
  -> SpeechSegmenter
  -> WavEncoder
  -> PythonWorkerClient
  -> Python asr_worker
  -> MiMo ASR API
  -> Subtitle timeline / reorder buffer / revision
  -> Source subtitle immediately visible
  -> Translation queue (V1.2, optional)
  -> Python translation_worker
  -> OpenAI-compatible Chat Completions endpoint
  -> Revision-aware translated text
  -> Bilingual floating subtitle window
  -> Append-only subtitle history jsonl
```

## Audio Pipeline

音频输入来自指定输出设备的 WASAPI loopback。第一版不做按应用或按窗口捕获。

内部统一音频格式：

```text
sample rate: 16000 Hz
channels: mono
sample format: PCM16
container for ASR: WAV
```

处理顺序：

```text
device native format
  -> channel mix to mono
  -> stateful WDL resample to 16kHz float
  -> convert to PCM16
  -> frame buffer
  -> VAD
  -> segment WAV bytes
```

先统一格式，再进入 VAD。实时捕获必须保持跨 WASAPI 回调的重采样状态，不能逐块重置插值相位。loopback 完全无数据时由静音 gap filler 补齐 32ms 静音帧，保证尾静音仍能推进。

## VAD And Segmentation

V1.0 以准确度优先，C# 侧内置 Silero VAD ONNX。正式运行缺少模型时阻止启动，不自动降级为 Energy VAD。

当前 Silero ONNX wrapper 的输入不仅包含 16kHz 下的 512-sample 帧，还必须在帧前拼接上一帧末尾 64 samples 的 context；8kHz 对应 256+32。state 和 context 都要在新会话或采样率切换时重置。

VAD 只判断短帧是否像语音。句子边界由 `SpeechSegmenter` 决定。

V1.0 固定模式基线参数：

```text
EndSilenceMs: 300
StartSpeechMs: 96
PreRollMs: 192
MinSegmentMs: 900
SoftBreakSilenceMs: 128
SoftMaxSegmentMs: 4000
HardMaxSegmentMs: 10000
OverlapMs: 600
```

V1.0 固定模式用户可配置项：

```text
断句等待: 200-800ms, 默认 300ms, 步进 50ms
最短片段: 300-3000ms, 默认 900ms
最长片段: 6000-20000ms, 默认 10000ms, 步进 1000ms
```

用户侧命名建议：

```text
断句等待
声音停止后等待多久生成字幕
```

以上固定 300ms 仅是 V1.0 基线。当前 V1.1 默认使用均衡自适应端点，初始值为 400ms；固定值模式仍保留 200-800ms 范围，用于复现 V1.0 行为或做对比测试。V1.1 的目标驱动单变量自适应端点如下：

| 模式 | 初始值 | 范围 |
|---|---:|---:|
| 低延迟 | 250ms | 200-400ms |
| 均衡 | 400ms | 280-600ms |
| 句子完整 | 600ms | 400-800ms |
| 固定值 | 用户输入 | 200-800ms |

`AdaptiveEndpointController` 位于 VAD 和 `SpeechSegmenter` 之间，只接收 speech/non-speech 决策、单调时间戳、切段事件和后续确认语音事件。它不读取原始音频、ASR 文本、LLM 语义或直播类别。

控制器状态：

- 最近 8 次有效停顿，样本最大年龄 15 秒。
- 至少 3 个样本后开始调整。
- P75 加 50ms 安全余量作为目标。
- 连续两次疑似误切后上调 50ms。
- 至少 6 个稳定样本后每次下调 25ms。
- 冷却 2 秒，每 10 秒最多调整 2 次。
- 10 秒没有确认语音时清空样本并逐步回归初始值。

完整停顿不超过 800ms 时判为疑似过早切段。该信号既用于后续端点学习，也用于 V1.1 字幕分组。

分段策略：

- 正常结束：检测到尾静音达到 `EndSilenceMs`。
- 起始预卷：确认语音后保留前置 `PreRollMs` 音频，避免切掉首音节。
- 软上限：连续说话达到 `SoftMaxSegmentMs` 后等待稳定的 `SoftBreakSilenceMs` 非语音区间。
- 硬上限：达到 `HardMaxSegmentMs` 后强制切片。
- overlap：硬切或连续切片时，下一段带上一段末尾 `OverlapMs` 的音频。
- 去重：文本层做本地 suffix/prefix 去重，避免 overlap 造成重复字幕。

设置页只开放 `MinSegmentMs` 和 `HardMaxSegmentMs`。`SoftMaxSegmentMs`、`SoftBreakSilenceMs` 与 `OverlapMs` 继续作为内部参数，避免用户配置出不一致的切片策略。最长片段仅在停止字幕后可修改。

ASR 识别语言在 schema V4 中固定为自动检测 `auto`。旧配置中的 `zh` 或 `en` 在加载时强制迁移为 `auto`，C# 运行时也只发送 `auto`。底层 Python worker 保留协议兼容能力，但产品 UI 不开放强制语言，且应用不对未知模型标签做猜测性文本清洗。

## ASR Worker Boundary

C# 与 Python 之间不使用临时 WAV 文件作为热路径边界。

边界定义：

```text
C#:
  capture / normalize / VAD / segment / WAV bytes

Python:
  receive JSON request
  call MiMo ASR
  return JSON response
```

通信方式：

```text
C# 启动长驻 Python worker 子进程
stdin 发送 JSON Lines 请求
stdout 接收 JSON Lines 响应
stderr 写入 worker 日志
```

请求示例：

```json
{
  "id": "seg-000123",
  "type": "transcribe",
  "sequence": 123,
  "startMs": 48120,
  "endMs": 54240,
  "audioFormat": "wav",
  "sampleRate": 16000,
  "language": "auto",
  "audioBase64": "..."
}
```

响应示例：

```json
{
  "id": "seg-000123",
  "type": "transcribe_result",
  "sequence": 123,
  "ok": true,
  "text": "识别出来的字幕内容",
  "latencyMs": 842
}
```

控制消息：

```json
{ "id": "ping-1", "type": "ping" }
```

```json
{ "id": "shutdown-1", "type": "shutdown" }
```

## ASR API

Python worker 使用 OpenAI Python SDK 调用 MiMo ASR。

V1.0 默认模型：

```text
mimo-v2.5-asr
```

设置页允许用户配置：

- API Key
- Base URL
- Model
- Language
- Timeout
- Max concurrency

`Language` 仅允许 `auto`、`zh`、`en`。Python worker 使用有界线程池执行 ASR，请求可以乱序返回，stdout 由写锁保证每行 JSON 完整。

API Key 明文存放在 `data/settings.json`。UI 中仍使用密码输入框，日志和诊断信息不得输出完整 API Key。

参考文档：

- MiMo ASR: https://mimo.mi.com/docs/zh-CN/quick-start/usage-guide/audio/Speech-Recognition
- ONNX Runtime C#: https://onnxruntime.ai/docs/get-started/with-csharp.html
- Silero VAD: https://github.com/snakers4/silero-vad

## Translation Worker Boundary

V1.2 使用独立 translation worker，不合并 ASR worker 与翻译 worker。

```text
C#:
  source subtitle / revision / context / queue / stale decision
  UI / history / retry / circuit breaker / worker lifecycle

Python translation worker:
  OpenAI client / prompt / extraBody / response normalization
  error classification / bounded request concurrency
```

通信继续使用 stdin/stdout UTF-8 JSON Lines。C# 先发送 `configure`，再发送 `translate`。API Key 通过 stdin 配置消息进入 worker，不放在命令行参数中。

translation worker 只接收文本，不接收 WAV 或 Base64 音频。它不管理 vLLM、Ollama、llama.cpp、LM Studio 等本地服务。

V1.2 只支持 OpenAI Chat Completions compatible 协议。用户手动填写 Base URL 和模型名，应用不自动补 `/v1`，并实时预览最终 `/chat/completions` 地址。

翻译请求使用内置版本化 Prompt、最多 3 组且 30 秒内的上下文、非流式响应和可选 extraBody 兼容模板。完整协议见 [v1.2-translation-protocol.md](v1.2-translation-protocol.md)。

## ASR Concurrency And Ordering

V1.0 允许有限并发：

```text
MaxConcurrentAsr: 2 by default
```

每个音频片段必须带：

- `sequence`
- `startMs`
- `endMs`

返回结果可能乱序。C# 侧通过 reorder buffer 保证字幕时间线尽量稳定。

策略：

- 正常情况下按 `sequence` 显示。
- 前序片段失败时标记失败并继续释放后续字幕。
- 慢段由 ASR timeout、worker 失败传播和停止时取消来避免永久阻塞。
- 每个已分配 sequence 必须提交 final 或 failed 终态，异常不能在 reorder buffer 中留下缺口。

## Subtitle Model

V1.0 不默认发送滚动 interim ASR 请求，但数据模型预留 interim。

```csharp
public enum SubtitleStatus
{
    Interim,
    Final,
    Failed
}

public sealed record SubtitleItem
{
    public long Sequence { get; init; }
    public string? UtteranceGroupId { get; init; }
    public int Revision { get; init; }
    public IReadOnlyList<long> ReplacesSequences { get; init; }
    public TimeSpan Start { get; init; }
    public TimeSpan End { get; init; }
    public DateTimeOffset? GeneratedAt { get; init; }
    public string SourceText { get; init; } = "";
    public string? TranslatedText { get; init; }
    public SubtitleStatus Status { get; init; }
}
```

V1.0 字幕策略：

- final / final-ish 片段为主。
- 预留 interim 状态和 UI 替换能力。
- V1.3 再评估滚动 interim ASR。

V1.1 自适应端点阶段先实现字幕 revision，而不实现 rolling interim：

- 第一个 ASR 结果立即显示。
- 完整停顿不超过 800ms 时，相邻 sequence 归入同一个 `utteranceGroupId`。
- group id 包含会话唯一前缀；revision 替换必须同时匹配 group id 和 sequence。
- 同组最多 3 段、最长 12 秒。
- 后续结果使用确定性去重合并，并更新历史中的同一行。
- 只有该组仍是悬浮窗当前字幕时才替换悬浮文本。
- ASR 失败或边界超限时关闭当前组。

V1.2 翻译只消费 final/final-ish source subtitle：

- 原文完成本地去重后立即显示。
- 每个 `utteranceGroupId + sourceRevision` 单独翻译。
- 完整译文返回后一次性补充，不显示 token 增量。
- source revision 后立即清除旧译文并重新翻译。
- stale translation result 不得覆盖最新原文。
- 翻译结果乱序返回时独立填充自己的字幕组。
- 已离开悬浮窗的旧字幕不因迟到译文重新出现。
- 翻译失败、背压或熔断不影响 source subtitle。

## Text Deduplication

V1.0 不调用任何 LLM 做字幕去重或合并，也不假设 ASR API 支持上下文合并。

只做本地确定性 suffix/prefix 去重：

```text
previousFinalText suffix
newText prefix
  -> longest overlap
  -> append non-overlap suffix from newText
```

规则：

- 中文重叠至少 3 个字符。
- 英文重叠至少 2 个词。
- 只处理最近 1-2 条 final 字幕。
- 不纠错。
- 不润色。
- 不语义改写。

## UI Architecture

V1.0 包含两个窗口：

```text
MainWindow:
  主页、字幕历史、设置、关于

FloatingSubtitleWindow:
  Always on top
  透明/半透明背景
  最近 1-3 个字幕组
```

主窗口页面：

- 主页：捕获、VAD、ASR worker/API、translation worker/API、字幕输出状态，最近错误和开始/停止按钮。
- 字幕历史：当天双语字幕、复制选中、复制最近、复制全部、清空历史。
- 设置：音频、VAD、识别服务、翻译、悬浮窗、快捷键和诊断参数。
- 关于：版本、数据目录、日志目录、复制诊断信息。

V1.2 翻译配置继续放在设置页，不新增导航项。默认页面只暴露翻译开关、目标语言、活动配置、验证状态和测试连接；模型详情放入 ContentDialog。

悬浮窗从单个 TextBlock 改为字幕组列表：

- 原文在上，译文在下。
- 原文字号强制迁移为 18，译文默认 90%。
- “最大行数”改为“显示字幕数”，默认 2，范围 1-3。
- 两层文本都自动换行，不显示目标语言标签或错误文本。
- 窗口高度最多为当前工作区 40%，超高时移除最旧字幕组。

悬浮窗模式：

```text
编辑模式:
  可拖动
  可缩放
  可调透明度/字号/行数
  不点击穿透

锁定模式:
  Always on top
  点击穿透
  只显示字幕
  可用快捷键退出锁定
```

全局快捷键：

```text
Ctrl+Alt+S: 开始/停止字幕
Ctrl+Alt+H: 隐藏/显示悬浮窗
Ctrl+Alt+L: 锁定/解锁悬浮窗
```

设置页允许关闭快捷键，并提示快捷键冲突。

## Process Lifecycle

Python workers 对用户不可见，由 C# 分别托管。

生命周期：

```text
用户打开 C# UI:
  不立即启动 worker

用户点击开始字幕:
  启动 asr worker
  翻译开启时启动 translation worker
  configure / ping
  启动音频捕获

用户点击停止字幕:
  停止音频捕获
  drain 或取消 ASR 队列
  translation queue 最多 drain 3 秒
  向两个 worker 发送 shutdown
  等待 workers 退出

用户关闭主窗口:
  退出整个程序
  停止捕获
  停止/取消队列
  向两个 worker 发送 shutdown
  2-3 秒后仍未退出则 Kill entire process tree
```

关闭主窗口就是退出整个程序。V1.0 不做最小化到托盘继续运行。

translation worker 跟随捕获会话启动和停止。捕获运行中禁止切换翻译开关、目标语言、活动配置、并发、超时和 extraBody 模板。translation worker 每个会话最多自动重启一次，第二次崩溃只停止翻译，不停止 ASR。

## Data Layout

发布版为绿色便携版。数据优先写到启动 exe 同目录下的 `data/`。

如果 `data/` 不可写，应用应明确提示错误，不静默 fallback 到 `%LocalAppData%`。

推荐目录：

```text
StreamTranslator/
  StreamTranslator.exe
  worker/
    asr_worker.exe
    translation_worker.exe
  models/
    silero_vad.onnx
  data/
    settings.json
    subtitles/
      2026-07-07.jsonl
    logs/
      app.log
      worker.log
    debug-audio/
      segments/
      vad/
      sessions/
    debug-translation/
    translation-evaluation/
```

V1.0 默认保存字幕历史，不保存音频片段。

V1.1 历史保持 append-only。原始字幕行继续保留，合并时追加 `subtitle_revision` 事件。读取历史时按稳定 `utteranceGroupId` 只物化最新 revision；旧版没有事件类型和分组字段的行按独立字幕兼容读取。

V1.2 成功译文继续追加 `translation_result`。读取时只选择与当前 source revision 匹配的最新成功译文。失败、跳过和背压状态可以追加 `translation_status`，普通历史 UI 不显示错误。历史不会自动补翻旧记录。

诊断模式开启后才保存：

- segment WAV
- VAD probability timeline
- session JSON

## Error Handling

V1.0 只对明确可恢复错误自动重试一次，不做无限重试。

建议策略：

- worker 启动失败：不重试，显示配置/依赖错误。
- worker 运行中崩溃：自动重启 1 次，仍失败则停止字幕并提示。
- ASR 网络超时：当前 segment 重试 1 次，仍失败则标记失败并继续后续段。
- API 401/403：不重试，停止字幕，提示检查 API Key。
- API 429：当前 segment 重试 1 次，仍失败则标记失败并提示限流；动态降低并发留到后续版本。
- 音频设备断开：若开启跟随默认设备，则尝试切换默认设备；否则停止字幕并提示重新选择设备。
- Translation worker crash：每会话自动重启 1 次，第二次只停止翻译。
- Translation 401/403、模型、配置或协议错误：停止本会话翻译，ASR 继续。
- Translation 网络、超时、429、5xx：单任务最多重试 1 次；连续失败进入 10-60 秒熔断。
- Translation backlog：队列最多 8，等待超过 10 秒允许丢弃旧译文，原文始终保留。

Python 错误响应包含 `errorKind`、`statusCode` 和 `retryable`。C# 对每个请求设置独立硬超时，并使用互斥锁保证整个运行期最多重启 worker 一次。

状态页按层显示：

- 音频捕获
- VAD
- ASR worker
- ASR API
- Translation worker
- Translation API / local service
- Subtitle output

## Diagnostics

V1.0 内置诊断模式，默认关闭。

开启后记录：

- 每帧 VAD probability
- 每段 start/end/duration
- 切段原因：silence / soft_max / hard_max
- overlap ms
- ASR latency
- ASR ok/error code
- dedup result
- 当前端点、调整方向与原因
- 有效停顿样本数、P75 和目标值
- 疑似过早切段与字幕 revision
- utterance group 大小、跨度和 ASR 调用次数
- 翻译请求、成功、失败、重试和 revision 重译次数
- 翻译队列、背压丢弃、stale result 和同语言跳过
- translation worker 重启、熔断和延迟 P50/P95
- 目标语言、模型名和本地/远程类型

V1.0 还包含离线音频回放/分段 CLI：

```text
StreamTranslator.Diagnostics.exe segment --input sample.wav
```

CLI 应复用同一套：

```text
AudioNormalize -> VAD -> SpeechSegmenter
```

输出：

- segment WAV
- segment JSON
- VAD timeline JSONL
- metrics JSON

V1.1 自适应算法、历史事件形状和验收标准的完整定义见 [v1.1-adaptive-vad.md](v1.1-adaptive-vad.md)。

同批音频的固定 300ms / 均衡自适应对比可使用：

```text
scripts/adaptive-vad-evaluate.ps1
```

V1.2 翻译评估使用：

```text
scripts/translation-evaluate.ps1
```

该脚本只在显式允许时调用真实 API。完整翻译架构、协议和验收标准见：

真实评估的专用 Key 只从当前进程环境变量 `STREAMTRANSLATOR_TRANSLATION_API_KEY` 读取，不进入命令行参数和评估产物。默认模式使用 fake client，不读取 Key、不访问网络。

- [v1.2-translation.md](v1.2-translation.md)
- [v1.2-translation-protocol.md](v1.2-translation-protocol.md)
- [v1.2-translation-test-plan.md](v1.2-translation-test-plan.md)
