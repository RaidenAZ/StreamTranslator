# StreamTranslator Decisions

本文档记录已确认的 V1.0 架构和产品决策。每条决策都应作为后续实现时的默认依据，除非有新的明确变更。

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

状态：Accepted

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

状态：Accepted

V1.0 只发 final / final-ish 音频片段。数据模型和 UI 预留 interim 替换能力。

滚动 interim ASR 放到：

```text
V1.1: 实验功能，默认关闭
V1.2: 若稳定，再考虑正式低延迟模式
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
