# UI 打磨分析报告（2026-07-26）

> **实施状态（2026-07-26）**：P1（除 #1 应用图标，按用户决定暂不实施）与 P2 全部条目（#2–#15）已在 bugfix 分支实现并通过单元测试与 UI 冒烟测试。P3 条目未实施。

范围：`src/StreamTranslator.App` 下的全部 UI 代码（`App.xaml`、`MainWindow.xaml/.cs`、`FloatingSubtitleWindow.xaml/.cs`）。
视角：视觉一致性、操作反馈、悬浮字幕窗体验、设置页体验、可访问性。功能正确性问题不在本报告范围（见 `code-review-2026-07-26.md`）。

总体评价：UI 骨架已经相当完整——Fluent 类型阶梯、卡片式布局、状态徽章、Snackbar、页面切换动画、AutomationId 覆盖都做到位了。剩下的主要是**一致性收口**（MessageBox 与 ContentDialog 混用、主题策略含糊）、**长耗时操作的反馈**（启动/停止/关闭无进度指示）和**悬浮窗的直播场景细节**（位置不记忆、锁定无指示、设置不即时生效）。

优先级说明：P1 = 用户能明显感知、建议发布前处理；P2 = 一致性与体验打磨；P3 = 锦上添花。

---

## P1 发布前建议处理

### 1. 应用无图标
`StreamTranslator.App.csproj` 没有 `ApplicationIcon`，主窗口、任务栏、Alt+Tab、悬浮窗全部是 WPF 默认图标。这是发布品质上最直观的一处欠缺。补一个 `.ico` 并在标题栏 `TitleBar.Header` 里加上小图标即可。

### 2. 系统 MessageBox 与 Fluent 风格割裂
四处仍在使用 Win32 风格的 `System.Windows.MessageBox`：

- 翻译 worker 启动失败降级询问（`MainWindow.xaml.cs:210`）
- 清空当天历史确认（`MainWindow.xaml.cs:404`）
- 翻译不可用降级询问（`MainWindow.xaml.cs:1002`）
- 删除翻译配置确认（`MainWindow.xaml.cs:1116`）

而翻译配置编辑器已经在用 wpf-ui 的 `ContentDialog`（`RootContentDialogHost` 已就位）。系统弹窗是灰色经典样式，和 Mica 背景 + 圆角卡片的主界面放在一起非常突兀。建议全部迁移到 `ContentDialog`（Yes/No 对应 Primary/Close），顺带获得深色主题适配。

### 3. 长耗时操作缺少进度反馈
- **启动/停止字幕**可能持续数秒（拉起 Python worker、健康检查），期间只有按钮禁用和徽章文字"启动中"。建议在 `StartStopButton` 内嵌一个小号 `ProgressRing`（wpf-ui 自带），或至少把按钮文字换成"启动中…"。
- **关闭窗口**时 `OnClosing` 会 `IsEnabled = false` 并最多等待 15 秒排空（`MainWindow.xaml.cs:103-113`），期间整个窗口变灰无任何说明，观感像程序卡死。建议显示一个"正在停止字幕并保存…"的覆盖层或对话框。
- **测试连接**只有一行"正在发送测试翻译..."文本（`MainWindow.xaml.cs:1155`），可加 `ProgressRing` 与按钮忙碌态。

### 4. 悬浮窗位置与大小不持久化
`FloatingSubtitleWindow` 每次启动都 `WindowStartupLocation="CenterScreen"`、`Width=760`。直播用户每次开播都要重新拖到习惯位置。建议把 `Left/Top/Width` 存入 `AppSettings.SubtitleWindow` 并在创建时还原（注意校验坐标仍在可见工作区内，防止显示器变更后窗口丢失）。这可能是悬浮窗最高频的体验痛点。

### 5. 锁定（点击穿透）状态没有任何可见指示
锁定后悬浮窗点击穿透（`FloatingSubtitleWindow.xaml.cs:173-184`），用户既看不出当前处于锁定态，也无法用鼠标解锁，只能靠记住 Ctrl+Alt+L。建议：

- 主窗口的"锁定/解锁"按钮改为反映当前状态（图标在 `LockClosed24` / `LockOpen24` 间切换，文案显示当前态），与"显示/隐藏悬浮窗"按钮的做法（`UpdateFloatingWindowButtonState`，`MainWindow.xaml.cs:1751`）对齐；
- 切换锁定时在悬浮窗上短暂显示一个锁形提示（1-2 秒淡出）。

### 6. 悬浮窗设置不即时生效、修改不即时保存
- 字号 / 显示行数 / 透明度改动只在下一次 `ShowFloatingWindow()` 时才 `ApplySettings`（`MainWindow.xaml.cs:800-803`）。拖透明度 Slider 时悬浮窗应实时预览——对这类"调到满意为止"的参数，即时反馈是刚需。
- `SaveSettingsAsync` 只在启动、关闭、翻译配置 CRUD 时调用。用户改完设置后若进程异常退出，改动全部丢失。建议设置变更后防抖（如 800ms）自动保存。

### 7. 首次使用无引导
全新用户打开应用，主页看不出需要先配置 API Key；直接点"开始字幕"后，校验错误以异常消息形式落进"问题"卡片（`ValidateForStart` 抛出 → `LastErrorText`），呈现生硬。建议：

- 未配置 API Key 时，主页状态区显示引导文案 + "前往设置"直达按钮；
- 启动校验失败时用 `ContentDialog` 列出缺失项并提供跳转，而不是当作运行错误展示。

---

## P2 一致性与体验打磨

### 8. 主题策略含糊，无主题设置项
`App.xaml:9` 固定 `Theme="Light"`，而 `MainWindow` 构造函数又调用 `SystemThemeWatcher.Watch(this)`（`MainWindow.xaml.cs:58`）跟随系统主题——两者语义冲突，实际行为取决于 wpf-ui 内部时序。建议：

- 明确策略并提供设置项：跟随系统 / 亮色 / 暗色；
- 做一轮暗色模式走查，重点是代码里手搭 UI 的兜底画刷（`Brushes.IndianRed`、`Brushes.DimGray`，`MainWindow.xaml.cs:1261,1488`）和自定义 RadioButton 模板的各态颜色。

### 9. 复制反馈不对称且不消失
复制成功只把 `SubtitleHistoryCopyStatusText` 设为"已复制"，之后**一直停留**直到下次操作；失败则有 Snackbar。建议统一：成功也走 Snackbar（或状态文字 2-3 秒后淡出），成功/失败视觉对称。

### 10. 音频设备列表不能刷新
`LoadAudioDevices` 只在 `OnLoaded` 时调用一次。用户开播中途插入 USB 声卡/耳机后，设置页看不到新设备，只能重启应用。最低成本方案是在设备下拉旁加一个刷新按钮；更完整的方案是订阅 `MMDeviceEnumerator` 的设备变更通知。

### 11. 悬浮窗翻译等待时显示空行
`FloatingSubtitleEntry.TranslationVisibility` 在 pending 且译文为空时返回 `Visible`（`FloatingSubtitleWindow.xaml.cs:218-221`），效果是原文下面出现一条空白行占位。占位本身是对的（避免译文到达时跳动），但空行观感差，建议显示"…"或三点呼吸动画作为翻译中的占位符。

### 12. 文案与单位不统一
- 设置页单位标注三种写法并存："最短片段 (ms)"、"请求超时 ms"、"固定断句等待"（无单位）。建议统一为"xxx (ms)"，或更进一步在 NumberBox 上显示单位后缀。
- 透明度 Slider 没有数值显示，建议旁边加百分比文本。
- "关于"页版本号硬编码"StreamTranslator V1.2"（`MainWindow.xaml:930`），诊断快照也硬编码 `Version = "V1.2"`（`MainWindow.xaml.cs:468`），而 csproj 已有 `<Version>1.2.0</Version>`。三处应收敛为从 assembly 读取，避免下个版本漏改。

### 13. 设置校验滞后
API Key / Base URL 为空或非法要等到点"开始字幕"才报错。建议失焦即校验（wpf-ui TextBox 支持错误态描边），把问题暴露在离输入最近的地方。

### 14. 运行状态路由依赖字符串匹配
`OnRuntimeStatusChanged` 靠 `status.Contains("ASR API")/"worker"/"VAD"/"捕获"` 决定更新哪个状态格（`MainWindow.xaml.cs:583-603`）。文案一改状态就会落错格子或丢失。建议 runtime 事件携带结构化的状态类别枚举，字符串只做展示。这同时是 `code-review` 报告中"MainWindow 重构"建议的一部分。

### 15. 内联样式应上收
- 标题栏与四个 `NavigationViewItem` 各自内联 `FontSize="12"`；
- VAD 模式分段控件的 `RadioButton` 模板（约 40 行）内联在 `MainWindow.xaml:464-503` 页面资源里。

建议统一移入 `App.xaml`（或独立 ResourceDictionary），后续新页面复用时不会漂移。分段控件宽度也硬编码 `Width="456"`，可改为按内容自适应。

---

## P3 锦上添花

### 16. 字幕历史增强
- 目前只能看"今天"，无日期切换；数据本身按天存 jsonl，加一个日期选择即可打开历史。
- 无搜索/过滤，长时间直播后上千条记录难以定位。
- 新字幕 `Insert(0)` 插入顶部：用户向下滚动回看时，视口内容会被不断推移。建议检测"用户不在顶部"时暂停自动插入位移（或显示"N 条新字幕"悬浮提示，点击回到顶部）。
- `SubtitleList.Items` 直接操作，全天数据量大时建议换 `ObservableCollection` + `ItemsSource`，并确认虚拟化生效。

### 17. 悬浮窗右键菜单
锁定、隐藏、临时调字号目前都要回主窗口或用快捷键。未锁定时给悬浮窗一个 ContextMenu（隐藏 / 锁定 / 字号 ± / 打开主窗口）会顺手很多。

### 18. 字幕可读性选项
悬浮窗白字仅靠 DropShadow 保证可读性，遇到亮色画面（雪景、白底 PPT）对比度会不足。可提供文字描边开关或背景不透明度快速调节（现有透明度设置已覆盖一部分）。

### 19. 快捷键不可自定义
Ctrl+Alt+S/H/L 硬编码（`MainWindow.xaml.cs:1699-1701`），与其他软件冲突时只能整体关闭快捷键。可在设置页提供改键（成本较高，视用户反馈决定）。

### 20. 多显示器细节
悬浮窗 `MaxHeight` 按 `SystemParameters.WorkArea`（主屏）计算（`FloatingSubtitleWindow.xaml.cs:97`），拖到分辨率不同的副屏后限高不准。可改用窗口当前所在屏幕的工作区。

### 21. 可访问性
- AutomationId 覆盖已经很好（利于 UI 测试）；下一步可给状态徽章、"问题"卡文本加 `AutomationProperties.LiveSetting="Polite"`，让屏幕阅读器感知状态变化。
- 输入电平 `ProgressBar` 可补 `AutomationProperties.Name="输入电平"`。

---

## 建议的处理顺序

| 批次 | 条目 | 主要理由 |
| --- | --- | --- |
| 第一批 | #1 图标、#2 ContentDialog 统一、#3 进度反馈 | 视觉品质与"是否卡死"的直接观感 |
| 第二批 | #4 悬浮窗位置记忆、#5 锁定指示、#6 设置即时生效/保存 | 直播主力场景的高频痛点 |
| 第三批 | #7 首次引导、#9 复制反馈、#10 设备刷新、#11 pending 占位、#12 文案统一 | 一致性收口 |
| 之后 | P2 剩余 + P3 | 按用户反馈排期 |

其中 #14（状态字符串路由）与 #15（样式上收）建议合并进 `code-review-2026-07-26.md` 里已提出的 MainWindow 渐进重构一起做，避免两次触碰同一批代码。
