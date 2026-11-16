# StreamTranslator

Windows 上的实时字幕工具。它把电脑正在播放的声音（直播、视频、网课、会议）实时转成文字，显示在置顶的悬浮字幕窗里。开启翻译后，原文下方会同时显示译文，形成双语字幕。

语音识别和翻译都调用你自己配置的云端 API，程序本身不包含语音识别模型。

## 功能

- **捕获电脑播放的声音**：录制指定输出设备（扬声器、耳机）的全部声音，不需要麦克风，也不需要虚拟声卡。可以跟随系统默认设备自动切换。
- **自动断句**：在本地用 Silero VAD 检测人声，在说话停顿处切段后再送去识别。断句支持低延迟、均衡（默认，按说话节奏自适应）、句子完整和固定值四种模式。
- **多家语音识别服务**：内置 MiMo ASR、智谱 GLM-ASR-2512、DeepSeek ASR，也可以接入其他兼容 OpenAI `audio/transcriptions` 接口的服务。识别语言自动检测。
- **可选双语翻译**：接入任意兼容 OpenAI Chat Completions 的模型，例如 DeepSeek、用 vLLM 部署的 Qwen、Ollama、LM Studio。目标语言可选中文、英语、德语、法语、日语。原文先显示，译文随后补上，翻译出错不影响原文字幕。
- **悬浮字幕窗**：始终置顶、半透明，可以调整字号、透明度和同时显示的字幕条数。锁定后鼠标点击会穿透字幕窗，不影响操作下面的窗口。
- **字幕历史**：自动保存当天的原文和译文，可以复制选中的、最近 10 条或当天全部字幕。
- **全局快捷键**：`Ctrl+Alt+S` 开始/停止字幕，`Ctrl+Alt+H` 隐藏/显示悬浮窗，`Ctrl+Alt+L` 锁定/解锁悬浮窗。
- **绿色便携**：解压即用，设置和字幕历史都保存在程序目录下的 `data\`。

## 快速开始

1. 安装 [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0)，选择 Windows x64 的 ".NET Desktop Runtime"。已经装过可以跳过。
2. 从 [Releases](https://github.com/RaidenAZ/StreamTranslator/releases) 下载最新的 `StreamTranslator-v*-win-x64.zip`，解压到一个普通文件夹。不要放在 `C:\Program Files` 这类需要管理员权限的目录，程序要在自己的目录下写入 `data\`。
3. 运行 `StreamTranslator.exe`，打开「设置 → 识别服务」，选择服务商并填写 API Key。
4. 在「设置 → 音频」确认输出设备，默认跟随系统。回到主页点击「开始字幕」。
5. 播放有人说话的视频或直播，字幕会显示在悬浮窗里。主页的「悬浮窗」按钮可以显示或隐藏它。

### 支持的识别服务

| 服务商 | 需要填写 | 参考文档 |
|---|---|---|
| MiMo ASR | API Key | [MiMo 语音识别](https://mimo.mi.com/docs/zh-CN/quick-start/usage-guide/audio/Speech-Recognition) |
| 智谱 GLM-ASR-2512 | API Key | [智谱开放平台](https://open.bigmodel.cn) |
| DeepSeek ASR | API Key | [DeepSeek ASR](https://api-docs.deepseek.com/zh-cn/guides/asr) |
| 自定义 | API Key、Base URL、模型名 | 需兼容 OpenAI `audio/transcriptions` 接口 |

API 用量按各平台的规则计费。

### 开启翻译（可选）

1. 在「设置 → 字幕翻译」点 `+` 添加模型配置，填写 Base URL、模型名和 API Key。Base URL 按服务商文档原样填写，例如 `https://api.deepseek.com`。程序只在后面拼接 `/chat/completions`，不会自动补 `/v1`。
2. 使用 DeepSeek 或 vLLM 部署的 Qwen 时，可以在「高级设置 → 请求兼容」里选择对应模板来关闭思考模式，降低翻译延迟。
3. 点「测试连接」确认可用，选择目标语言，再打开「启用翻译」。

翻译相关的设置只能在字幕停止时修改。

## 工作原理

```text
电脑播放的声音（WASAPI loopback）
  → 转为 16kHz 单声道
  → Silero VAD 检测人声，在停顿处切段
  → 识别服务 API 转成文字
  → 悬浮窗显示原文，写入字幕历史
  → （可选）翻译模型 API 生成译文，补到原文下方
```

识别按句进行，不是逐字流式输出。一句话说完、检测到停顿后字幕才会出现，延迟取决于断句等待时间和识别接口的响应速度。没有人声的片段（背景音乐、音效）会被跳过，不显示字幕。

## 隐私与数据

- 切出的语音片段会上传到你选择的识别服务。开启翻译后，字幕文本和最近几句上下文会发送到你配置的翻译 API。除此之外，程序不会向其他地方发送数据。
- API Key 以明文保存在 `data\settings.json`。分享程序目录前，请先删除或清空这个文件。
- 字幕历史按天保存在 `data\subtitles\`。程序默认不保存音频。只有开启「设置 → 诊断模式」后，才会把分段音频和 VAD 时间线写入 `data\debug-audio\`，用于排查问题。

## 常见问题

**点了开始，但一直没有字幕**

先看主页的「输入电平」是否随声音跳动。如果不动，说明选中的输出设备没有在播放声音，去「设置 → 音频」换一个设备。电平正常但没有字幕时，看主页的「识别接口」状态和「问题」卡片，常见原因是 API Key 错误或额度用完。

**能只识别某一个应用的声音吗？**

目前不能。程序捕获的是整个输出设备的声音，同一设备上的其他声音（如提示音）也会被识别。

**提示 `data` 目录不可写**

把程序移到一个当前用户有写权限的目录，比如桌面或 `D:\Tools`。

## 系统要求

- Windows 10 或 Windows 11，x64
- [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0)
- 能访问所选识别和翻译服务的网络

发行包已经内置识别和翻译 worker 以及 Silero VAD 模型，不需要另外安装 Python。

## 从源码构建

需要 .NET 10 SDK 和 Python 3.12 或更高版本（CI 使用 3.12）。

```powershell
# 打包便携版，输出到 artifacts\StreamTranslator
# 会自动下载并校验 Silero VAD 模型，并在独立的 venv 中用 PyInstaller 打包 Python worker
scripts\publish.ps1

# .NET 测试
dotnet test StreamTranslator.sln

# Python worker 测试（需要在 python 目录下运行）
cd python
python -m pip install -r requirements-dev.txt
python -m pytest tests
```

项目结构：

| 目录 | 内容 |
|---|---|
| `src/StreamTranslator.App` | WPF 主程序：主窗口、悬浮字幕窗、运行时编排 |
| `src/StreamTranslator.Audio` | 音频捕获、重采样、VAD、切段 |
| `src/StreamTranslator.Core` | 配置、字幕排序/去重/修订、翻译队列、worker 协议 |
| `src/StreamTranslator.Diagnostics` | 离线音频切段诊断命令行工具 |
| `python/` | ASR 和翻译 worker，通过 stdin/stdout JSON Lines 与主程序通信 |
| `scripts/` | 打包、模型下载、冒烟测试和评估脚本 |
| `tests/` | .NET 单元测试 |

## 设计文档

- [architecture.md](architecture.md)：系统架构、音频链路、VAD、worker 边界、UI、数据目录和运行时生命周期
- [decisions.md](decisions.md)：已确认的关键技术和产品决策
- [v1.0-plan.md](v1.0-plan.md)：V1.0 范围、完成标准和测试策略
- [v1.1-adaptive-vad.md](v1.1-adaptive-vad.md)：自适应断句、字幕分组和历史修订
- [v1.2-translation.md](v1.2-translation.md)：翻译字幕的产品行为、配置、队列和 UI
- [v1.2-translation-protocol.md](v1.2-translation-protocol.md)：translation worker 的 JSONL 消息、Prompt 和错误契约
- [v1.2-translation-test-plan.md](v1.2-translation-test-plan.md)：翻译功能的测试计划和验收清单
- [v1.3-sentence-accumulator.md](v1.3-sentence-accumulator.md)：跨片段的句子累积，改善翻译连贯性
- [v1.0-v1.1-release-backlog.md](v1.0-v1.1-release-backlog.md)：V1.0/V1.1 发布收口和延期项
- [../python/README.md](../python/README.md)：Python worker 的环境变量和各识别服务的协议差异