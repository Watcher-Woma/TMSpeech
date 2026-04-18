# 英语-中文实时字幕翻译功能需求文档

> 版本：1.0-draft
> 日期：2026-04-18
> 项目：TMSpeech

---

## 1. 概述

### 1.1 功能目标

在 TMSpeech 现有的实时语音识别字幕展示基础上，新增英中实时翻译功能。当识别器输出英文字幕时，翻译插件在本地将英文翻译为中文，主窗口同步展示原始英文字幕与中文翻译字幕。所有翻译计算完全由本地 CPU/GPU 完成，不依赖任何云端 API。

### 1.2 核心约束

| 约束项 | 要求 |
|--------|------|
| 翻译方式 | 纯本地离线推理，禁止调用远程 API |
| 运行平台 | Windows 10 17763+（与现有项目一致） |
| 目标框架 | .NET 6.0 |
| 插件架构 | 遵循现有 `IPlugin` + `ITranslator` 插件体系 |
| 翻译延迟 | 从识别器输出句子完成到翻译结果展示 ≤ 1.5s（短句 ≤ 0.8s） |
| 内存增量 | 翻译模块加载后运行时内存增量 ≤ 1.5 GB |

### 1.3 术语定义

| 术语 | 定义 |
|------|------|
| 源文本 | 识别器输出的原始英文字幕文本 |
| 译文 | 翻译插件输出的中文字幕文本 |
| TextChanged | 识别器的临时结果事件，表示正在识别中的片段 |
| SentenceDone | 识别器的句子完成事件，表示一句话识别结束 |
| CT2 | CTranslate2，高效的 Transformer 推理库 |
| OPUS-MT | 基于 MarianNMT 的离线神经机器翻译模型系列 |

---

## 2. 系统架构

### 2.1 整体数据流

```
┌──────────────┐    byte[]     ┌──────────────────┐  TextChanged   ┌────────────┐
│  AudioSource │──────────────▶│    Recognizer     │───────────────▶│            │
│   (插件)     │               │    (插件)         │  SentenceDone  │            │
└──────────────┘               └──────────────────┘───────────────▶│            │
                                                                 │ JobManager │
                                                                 │  (调度层)  │
┌──────────────┐  TranslatedTextEvent                   ┌─────────│            │
│  Translator  │◀───────────────────────────────────────│ 翻译调度 │            │
│   (插件)     │                                        └─────────└────────────┘
└──────┬───────┘                                                   │
       │ TranslationDone                                          │ TextChanged
       │ Translating (临时结果)                                    │ SentenceDone
       ▼                                                           ▼
┌──────────────────────────────────────────────────────────────────────────┐
│                          MainViewModel                                    │
│   Text (原文)          TranslatedText (译文)                               │
│   HistoryTexts (原文)  TranslatedHistoryTexts (译文)                       │
└──────────────────────┬───────────────────────────────────┬───────────────┘
                       │                                   │
                       ▼                                   ▼
              ┌─────────────────┐                ┌──────────────────┐
              │  CaptionView     │                │  CaptionView     │
              │  (原文字幕行)    │                │  (译文字幕行)    │
              └─────────────────┘                └──────────────────┘
```

### 2.2 翻译触发时机

翻译在两个时机触发，对应两种事件：

| 事件 | 触发条件 | 翻译策略 | 用途 |
|------|----------|----------|------|
| `SentenceDone` | 识别器判定一句话结束 | 完整翻译，上下文拼接 | 产出高质量定稿译文 |
| `TextChanged` | 识别器输出临时片段 | 增量翻译（仅翻译新增词段） | 实时预览，减少用户等待感 |

**关键设计**：`TextChanged` 事件的临时翻译结果由 `SentenceDone` 的定稿翻译结果覆盖，确保最终展示的是高质量翻译。

### 2.3 ITranslator 接口扩展

现有 `ITranslator` 接口过于简单（仅 `string Translate(string text)`），无法满足异步翻译、上下文传递和事件通知的需求。需扩展如下：

```csharp
// TMSpeech.Core/Plugins/ITranslator.cs

public class TranslationEventArgs : EventArgs
{
    public string OriginalText { get; set; }     // 原始文本
    public string TranslatedText { get; set; }   // 翻译结果
    public bool IsFinal { get; set; }            // true=定稿翻译, false=临时翻译
}

public interface ITranslator : IPlugin
{
    /// <summary>
    /// 翻译完成事件（包括临时翻译和定稿翻译）
    /// </summary>
    event EventHandler<TranslationEventArgs> TranslationCompleted;

    /// <summary>
    /// 请求翻译。实现应异步执行，通过 TranslationCompleted 事件返回结果。
    /// </summary>
    /// <param name="text">待翻译文本</param>
    /// <param name="isFinal">true 表示句子完成的定稿翻译，false 表示临时片段翻译</param>
    void TranslateAsync(string text, bool isFinal);

    /// <summary>
    /// 设置翻译上下文（前序已完成句子的原文和译文），用于提升翻译连贯性
    /// </summary>
    /// <param name="context">上下文项列表，每项包含原文和译文</param>
    void SetContext(IReadOnlyList<TranslationContextItem> context);
}

public class TranslationContextItem
{
    public string SourceText { get; set; }       // 原文
    public string TranslatedText { get; set; }   // 译文
}
```

**向后兼容**：保留原有的 `string Translate(string text)` 同步方法作为可选的便捷接口，但核心流程使用 `TranslateAsync`。

---

## 3. 翻译插件实现

### 3.1 插件标识

| 属性 | 值 |
|------|-----|
| 插件名称 | TMSpeech.Translator.LocalEnZh |
| GUID | 待生成（实现时确定） |
| 类型 | ITranslator 插件 |
| 目标框架 | net6.0 |
| EnableDynamicLoading | true |

### 3.2 翻译引擎选型

#### 推荐方案：CTranslate2 + OPUS-MT

| 评估维度 | CTranslate2 + OPUS-MT | ONNX Runtime + Hugging Face | Bergamot Translator |
|----------|----------------------|----------------------------|---------------------|
| 推理速度 | 快（CPU 优化，INT8 量化） | 中等 | 快 |
| 模型大小 | ~300 MB | ~500 MB+ | ~80 MB（小模型） |
| 翻译质量 | 高（OPUS-MT 大规模训练） | 高 | 中等 |
| .NET 集成 | 需 P/Invoke 或 CLI 子进程 | 有 NuGet 包 | 需子进程 |
| 上下文支持 | 支持（拼接上下文到输入） | 支持 | 有限 |

**最终选择**：采用 **子进程模式** 运行 CTranslate2 Python 推理服务，与现有 `CommandRecognizer` 的架构思路一致。原因：

1. CTranslate2 的 Python API 最成熟稳定，ONNX/CT2 C 库的 .NET 绑定维护不活跃
2. 与项目已有的 `CommandRecognizer` 外部进程模式设计理念一致
3. Python 进程可独立管理 GPU 资源，不与主程序的 Sherpa-Onnx 产生冲突
4. 支持模型热加载和上下文管理

#### 通信协议

子进程通过 **stdin/stdout JSON 行协议** 与主程序通信：

```jsonc
// 主程序 → 翻译进程（stdin）
{
  "id": "uuid-1234",           // 请求唯一标识
  "type": "translate",         // 请求类型
  "text": "Hello world",       // 待翻译文本
  "is_final": true,            // 是否为定稿翻译
  "context": [                 // 上下文（最多3条）
    { "source": "Hi there", "target": "你好" }
  ]
}

// 翻译进程 → 主程序（stdout）
{
  "id": "uuid-1234",           // 对应请求ID
  "type": "result",            // 响应类型
  "text": "你好世界",           // 翻译结果
  "is_final": true,            // 是否为定稿翻译
  "latency_ms": 230            // 翻译耗时（调试用）
}

// 翻译进程 → 主程序（stderr）
// 日志信息，写入翻译日志文件
```

### 3.3 翻译进程架构

```
translator_worker.py
├── 加载 CTranslate2 模型（启动时一次性加载）
├── 监听 stdin，解析 JSON 请求
├── 调用 ct2.Translate() 执行翻译
│   ├── 拼接上下文到源文本
│   └── beam_size=1（临时翻译）/ beam_size=4（定稿翻译）
├── 将结果以 JSON 格式写入 stdout
└── stderr 输出运行日志
```

### 3.4 模型管理

| 模型项 | 说明 |
|--------|------|
| 模型来源 | OPUS-MT `opus-mt-en-zh` 或 Helsinki-NLP/opus-mt-en-zh |
| 模型格式 | CTranslate2 转换后的优化格式 |
| 模型大小 | ~300 MB |
| 分发方式 | 通过现有 `ResourceManager` 系统下载安装 |
| 模型类型 | 在 `ModuleInfoTypeEnums` 中新增 `translator_model` 类型 |

模型安装流程复用现有的资源管理系统：

```
ResourceManager
  └── 发现 translator_model 类型资源
        └── 下载压缩包
              └── 解压到 resources/{model_id}/
                    └── 写入 tmmodule.json（含 translator_model_path 字段）
```

### 3.5 上下文连贯性维持

#### 策略一：拼接上下文（推荐）

将前序句对作为上下文拼接到翻译输入中：

```
源文本输入格式：
"<s>前序原文1</s><t>前序译文1</t><s>前序原文2</s><t>前序译文2</t><s>当前英文</s>"
```

- 保留最近 **3 轮**句对作为上下文
- 上下文在 `SentenceDone` 定稿翻译时写入，`TextChanged` 临时翻译时只读
- 超过 3 轮的上下文丢弃，避免输入过长导致延迟增加

#### 策略二：缓存前序翻译结果

- 维护一个 `ConcurrentQueue<TranslationContextItem>`（最大容量 3）
- 每次定稿翻译完成后，将原文+译文入队
- 临时翻译时，复用上一个定稿翻译的上下文快照

### 3.6 配置编辑器

翻译插件的配置项：

| 配置键 | 类型 | 默认值 | 说明 |
|--------|------|--------|------|
| model | string | "" | 选择的翻译模型 ID（从 ResourceManager 获取） |
| custom_model_path | string | "" | 自定义模型路径（高级用户） |
| beam_size | int | 4 | 定稿翻译的 beam 大小 |
| max_context_length | int | 3 | 上下文句对数量 |
| max_batch_size | int | 1 | 最大批处理大小 |
| compute_type | string | "auto" | 计算精度：auto/int8/int16/float16 |
| python_path | string | "" | Python 解释器路径（为空则自动检测） |

---

## 4. JobManager 调度层改造

### 4.1 新增翻译调度逻辑

在 `JobManagerImpl` 中新增翻译相关逻辑：

```csharp
// JobManagerImpl 新增成员
private ITranslator? _translator;
private ConcurrentQueue<TranslationContextItem> _translationContext = new();
private const int MaxContextLength = 3;

// 新增事件
public event EventHandler<TranslationEventArgs> TranslationCompleted;
```

### 4.2 翻译初始化

```csharp
private void InitTranslator()
{
    var configTranslator = ConfigManagerFactory.Instance
        .Get<string>(TranslatorConfigTypes.Translator);
    var config = ConfigManagerFactory.Instance
        .Get<string>(TranslatorConfigTypes.GetPluginConfigKey(configTranslator));

    if (string.IsNullOrEmpty(configTranslator)) return; // 翻译器为可选功能

    _translator = _pluginManager.Translators[configTranslator];
    if (_translator != null)
    {
        _translator.LoadConfig(config);
        _translator.TranslationCompleted -= OnTranslationCompleted;
        _translator.TranslationCompleted += OnTranslationCompleted;
        _translator.ExceptionOccured -= OnPluginRunningExceptionOccurs;
        _translator.ExceptionOccured += OnPluginRunningExceptionOccurs;
    }
}
```

### 4.3 事件转发逻辑

```csharp
private void OnRecognizerOnTextChanged(object? sender, SpeechEventArgs args)
{
    currentText = args.Text.Text;
    // ... 现有敏感词检测逻辑 ...

    // 新增：触发临时翻译
    if (_translator != null)
    {
        _translator.SetContext(_translationContext.ToList());
        _translator.TranslateAsync(args.Text.Text, isFinal: false);
    }

    OnTextChanged(args);
}

private void OnRecognizerOnSentenceDone(object? sender, SpeechEventArgs args)
{
    // ... 现有日志写入逻辑 ...

    // 新增：触发定稿翻译
    if (_translator != null)
    {
        _translator.SetContext(_translationContext.ToList());
        _translator.TranslateAsync(args.Text.Text, isFinal: true);
    }

    _disableInThisSentence = false;
    OnSentenceDone(args);
    currentText = "";
}

private void OnTranslationCompleted(object? sender, TranslationEventArgs args)
{
    // 定稿翻译完成后，更新上下文缓存
    if (args.IsFinal)
    {
        _translationContext.Enqueue(new TranslationContextItem
        {
            SourceText = args.OriginalText,
            TranslatedText = args.TranslatedText
        });
        while (_translationContext.Count > MaxContextLength)
            _translationContext.TryDequeue(out _);
    }

    // 向上转发
    TranslationCompleted?.Invoke(this, args);
}
```

### 4.4 新增配置类型

```csharp
// ConfigTypes.cs 新增
public static class TranslatorConfigTypes
{
    public const string SectionName = "translator";

    public const string Translator = "translator.source";
    public const string EnableTranslation = "translator.enable";
    public const string ShowOriginal = "translator.showOriginal";

    public static string GetPluginConfigKey(string pluginId)
    {
        return $"plugin.{pluginId}.config";
    }

    private static Dictionary<string, object> _defaultConfig => new()
    {
        { Translator, "" },
        { EnableTranslation, false },
        { ShowOriginal, true },
    };

    public static Dictionary<string, object> DefaultConfig => _defaultConfig;
}
```

---

## 5. 延迟最小化策略

### 5.1 分层延迟目标

| 阶段 | 目标延迟 | 策略 |
|------|----------|------|
| SentenceDone → 翻译请求发出 | < 5 ms | 事件直推，无线程调度 |
| 翻译进程接收 → 推理完成 | < 800 ms | CTranslate2 INT8 量化 + beam_size=4 |
| 推理完成 → UI 渲染 | < 50 ms | 主线程 Invoke / ReactiveUI 调度 |
| TextChanged 临时翻译 | < 500 ms | beam_size=1 + 增量翻译 |
| **端到端（SentenceDone → UI 显示）** | **< 1.5 s** | |

### 5.2 具体优化措施

#### 5.2.1 推理层优化

| 措施 | 说明 | 预期收益 |
|------|------|----------|
| INT8 量化 | CTranslate2 原生支持 INT8 量化推理 | 速度提升 2-3x，内存减少 50% |
| beam_size 动态调整 | 临时翻译用 beam_size=1，定稿翻译用 beam_size=4 | 临时翻译速度提升 2-3x |
| 模型预加载 | 子进程启动时即加载模型到内存 | 消除首次翻译冷启动延迟 |
| 批处理抑制 | `max_batch_size=1`，避免多请求排队 | 单次翻译延迟稳定 |

#### 5.2.2 通信层优化

| 措施 | 说明 | 预期收益 |
|------|------|----------|
| 子进程保活 | 翻译进程随主程序启动，不按需创建 | 消除进程启动延迟 |
| stdin/stdout 管道 | 避免网络栈开销 | 延迟 < 1ms |
| JSON 行协议 | 每行一个完整 JSON，避免粘包 | 简单可靠 |
| 异步非阻塞 | 主线程仅发送请求，不等待结果 | UI 不卡顿 |

#### 5.2.3 调度层优化

| 措施 | 说明 | 预期收益 |
|------|------|----------|
| 临时翻译请求节流 | TextChanged 事件 200ms 内只发送一次翻译请求 | 减少无效推理 |
| 定稿翻译优先级 | 定稿翻译请求可中断正在执行的临时翻译 | 定稿结果更快展示 |
| 翻译结果覆盖 | 定稿翻译结果覆盖同句的临时翻译结果 | 保证最终质量 |

### 5.3 临时翻译节流实现

```csharp
// JobManagerImpl 中实现节流
private DateTime _lastPartialTranslationTime = DateTime.MinValue;
private const int PartialTranslationThrottleMs = 200;

private void OnRecognizerOnTextChanged(object? sender, SpeechEventArgs args)
{
    // ... 现有逻辑 ...

    if (_translator != null)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastPartialTranslationTime).TotalMilliseconds >= PartialTranslationThrottleMs)
        {
            _lastPartialTranslationTime = now;
            _translator.SetContext(_translationContext.ToList());
            _translator.TranslateAsync(args.Text.Text, isFinal: false);
        }
    }

    OnTextChanged(args);
}
```

---

## 6. 翻译准确性保障

### 6.1 模型选择

- **主模型**：`Helsinki-NLP/opus-mt-en-zh`，基于 OPUS 并行语料训练，英中对齐质量高
- **备选模型**：`Helsinki-NLP/opus-mt-tc-big-en-zh`，更大的 TC 版本，质量更高但推理稍慢
- 模型评估指标：BLEU ≥ 30（在 WMT 英中测试集上）

### 6.2 上下文连贯性

- 通过 `SetContext` 传递最近 3 轮句对，解决代词指代和话题延续问题
- 定稿翻译使用完整上下文，临时翻译使用上一次定稿时的上下文快照
- 上下文窗口大小可配置（`max_context_length`）

### 6.3 临时翻译 → 定稿翻译覆盖

```
时间线：
  t0: Recognizer TextChanged  "Hello"         → 临时翻译 "你好"
  t1: Recognizer TextChanged  "Hello world"   → 临时翻译 "你好世界"
  t2: Recognizer SentenceDone "Hello world!"  → 定稿翻译 "你好世界！"  ← 覆盖 t1 结果
```

- UI 始终展示最新的翻译结果
- 当 `isFinal=true` 的翻译结果到达时，标记该句翻译为定稿，不再被后续临时结果覆盖
- 使用请求 ID 关联原文和译文，确保覆盖关系正确

### 6.4 后处理规则

| 规则 | 说明 |
|------|------|
| 去除重复 | 如果译文与原文完全相同（如专有名词），不在译文行重复展示 |
| 标点修正 | 中文译文应使用中文标点（，。！？） |
| 空文本过滤 | 空字符串或纯空白文本不触发翻译 |
| 长度截断 | 超过 500 字符的输入截断翻译，避免超时 |

---

## 7. 用户界面交互规范

### 7.1 主窗口字幕展示

#### 7.1.1 双行字幕布局

在现有 `CaptionView` 下方新增译文展示行：

```
┌────────────────────────────────────────────────┐
│  [▶] [■] 🔴 00:12:34  [📖] [🔒] [⚙]          │  ← 控制栏
├────────────────────────────────────────────────┤
│                                                │
│  Hello, welcome to the conference.             │  ← 原文字幕（英文）
│  你好，欢迎参加会议。                              │  ← 译文字幕（中文）
│                                                │
└────────────────────────────────────────────────┘
```

#### 7.1.2 CaptionView 改造

`CaptionView` 新增译文相关属性：

```csharp
// CaptionView.axaml.cs 新增属性
public static readonly StyledProperty<string> TranslatedTextProperty =
    AvaloniaProperty.Register<CaptionView, string>("TranslatedText");

public static readonly StyledProperty<Color> TranslatedFontColorProperty =
    AvaloniaProperty.Register<CaptionView, Color>("TranslatedFontColor", Colors.LightGray);

public static readonly StyledProperty<int> TranslatedFontSizeProperty =
    AvaloniaProperty.Register<CaptionView, int>("TranslatedFontSize", 32);

public string TranslatedText { get; set; }
public Color TranslatedFontColor { get; set; }
public int TranslatedFontSize { get; set; }
```

#### 7.1.3 CaptionView AXAML 布局

```xml
<!-- CaptionView.axaml 核心布局 -->
<StackPanel VerticalAlignment="Bottom" Canvas.Bottom="0" Width="{Binding $parent.Bounds.Width}">
    <!-- 原文字幕 -->
    <TextBlock FontWeight="Bold"
               Foreground="{Binding #root.FontColor, Converter={StaticResource ColorToBrushConverter}}"
               FontSize="{Binding #root.FontSize}"
               FontFamily="{Binding #root.FontFamily}"
               TextAlignment="{Binding #root.TextAlign}"
               Text="{Binding #root.Text}" />
    <!-- 译文字幕（仅在翻译功能开启且有译文时显示） -->
    <TextBlock FontWeight="Normal"
               Foreground="{Binding #root.TranslatedFontColor, Converter={StaticResource ColorToBrushConverter}}"
               FontSize="{Binding #root.TranslatedFontSize}"
               FontFamily="{Binding #root.FontFamily}"
               TextAlignment="{Binding #root.TextAlign}"
               Text="{Binding #root.TranslatedText}"
               IsVisible="{Binding #root.HasTranslation}" />
</StackPanel>
```

### 7.2 显示模式

用户可在配置窗口中选择以下显示模式：

| 模式 | 说明 | 对应配置值 |
|------|------|-----------|
| 仅原文 | 不展示翻译，与现有行为一致 | `original_only` |
| 仅译文 | 只展示中文翻译 | `translation_only` |
| 双语对照 | 上方原文，下方译文（默认） | `bilingual` |
| 交替展示 | 原文显示后短暂延迟切换为译文 | `alternating` |

配置键：`translator.displayMode`，默认值 `bilingual`

### 7.3 配置窗口新增标签页

在 `ConfigWindow` 中新增 **「翻译」** 标签页，位于「识别器」标签页之后：

```
[通用] [外观] [音频] [识别器] [翻译] [通知]
                              ^^^^ 新增
```

翻译标签页内容：

| 配置项 | 控件类型 | 说明 |
|--------|----------|------|
| 启用翻译 | CheckBox | 总开关 |
| 翻译器选择 | ComboBox | 从已加载的 Translator 插件列表选择 |
| 翻译器配置 | PluginConfigView | 动态加载选中翻译器的配置界面 |
| 显示模式 | ComboBox | original_only / translation_only / bilingual / alternating |
| 显示原文 | CheckBox | 双语模式下是否显示原文（默认 true） |
| 译文颜色 | ColorPicker | 译文文字颜色 |
| 译文字号 | NumericUpDown | 译文文字大小 |

### 7.4 历史记录窗口

`HistoryWindow` 中的历史条目需同时展示原文和译文：

```xml
<!-- HistoryView 条目模板改造 -->
<DockPanel Margin="8,4">
    <TextBlock Text="{Binding TimeStr}" Width="100" />
    <StackPanel>
        <SelectableTextBlock Text="{Binding Text}" TextWrapping="Wrap" />
        <SelectableTextBlock Text="{Binding TranslatedText}" TextWrapping="Wrap"
                             Foreground="Gray" FontSize="12" />
    </StackPanel>
</DockPanel>
```

`TextInfo` 类扩展：

```csharp
public class TextInfo
{
    public DateTime Time { get; set; }
    public string TimeStr => Time.ToString("T");
    public string Text { get; set; }
    public string? TranslatedText { get; set; }   // 新增：译文
}
```

### 7.5 托盘菜单

`TrayMenu` 新增菜单项：

| 菜单项 | 功能 |
|--------|------|
| 开启/关闭翻译 | 快捷切换翻译功能开关 |
| 显示原文 | 切换是否显示原文字幕 |

---

## 8. MainViewModel 改造

### 8.1 新增属性

```csharp
// MainViewModel 新增

[ObservableAsProperty]
public string TranslatedText { get; }         // 当前译文文本

[ObservableAsProperty]
public bool TranslationEnabled { get; }       // 翻译功能是否启用

[ObservableAsProperty]
public bool ShowOriginal { get; }             // 是否显示原文

public ObservableCollection<TextInfo> TranslatedHistoryTexts { get; } = new();
```

### 8.2 事件订阅

```csharp
// MainViewModel 构造函数中新增

// 订阅翻译完成事件
Observable.FromEventPattern<TranslationEventArgs>(
        p => _jobManager.TranslationCompleted += p,
        p => _jobManager.TranslationCompleted -= p)
    .ObserveOn(RxApp.MainThreadScheduler)
    .Subscribe(x =>
    {
        var args = x.EventArgs;

        // 更新当前译文
        TranslatedText = args.TranslatedText;

        // 如果是定稿翻译，更新历史记录中的译文
        if (args.IsFinal && HistoryTexts.Count > 0)
        {
            var lastItem = HistoryTexts.Last();
            if (lastItem.Text == args.OriginalText)
            {
                lastItem.TranslatedText = args.TranslatedText;
            }
        }
    });

// 翻译开关状态
var translationEnabled = Observable.Return(
        ConfigManagerFactory.Instance.Get<bool>(TranslatorConfigTypes.EnableTranslation))
    .Merge(
        Observable.FromEventPattern<ConfigChangedEventArgs>(
                p => ConfigManagerFactory.Instance.ConfigChanged += p,
                p => ConfigManagerFactory.Instance.ConfigChanged -= p)
            .Where(x => x.EventArgs.Contains(TranslatorConfigTypes.EnableTranslation))
            .Select(_ => ConfigManagerFactory.Instance.Get<bool>(TranslatorConfigTypes.EnableTranslation)))
    .ObserveOn(RxApp.MainThreadScheduler);

translationEnabled.ToPropertyEx(this, x => x.TranslationEnabled);
```

---

## 9. 构建集成

### 9.1 新增项目

```
src/Plugins/TMSpeech.Translator.LocalEnZh/
├── TMSpeech.Translator.LocalEnZh.csproj
├── LocalEnZhTranslator.cs            # ITranslator 实现
├── LocalEnZhTranslatorConfigEditor.cs # 配置编辑器
├── TranslatorWorkerClient.cs          # 子进程管理器
├── tmmodule.json                      # 模块元数据
└── worker/
    ├── translator_worker.py           # Python 翻译推理进程
    └── requirements.txt               # Python 依赖
```

### 9.2 csproj 配置

```xml
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <TargetFramework>net6.0</TargetFramework>
        <ImplicitUsings>enable</ImplicitUsings>
        <Nullable>enable</Nullable>
        <EnableDynamicLoading>true</EnableDynamicLoading>
    </PropertyGroup>

    <ItemGroup>
        <ProjectReference Include="..\..\TMSpeech.Core\TMSpeech.Core.csproj">
            <Private>false</Private>
            <ExcludeAssets>runtime</ExcludeAssets>
        </ProjectReference>
    </ItemGroup>

    <ItemGroup>
        <None Update="tmmodule.json">
            <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
        </None>
        <!-- Python worker 文件一并复制 -->
        <None Update="worker\**\*">
            <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
        </None>
    </ItemGroup>
</Project>
```

### 9.3 tmmodule.json

```json
{
    "id": "TMSpeech.Translator.LocalEnZh",
    "version": 1,
    "name": "本地英中翻译器",
    "desc": "基于 CTranslate2 的离线英中翻译插件",
    "type": "plugin",
    "assemblies": [
        "TMSpeech.Translator.LocalEnZh.dll"
    ]
}
```

### 9.4 TMSpeech.csproj 构建适配

无需修改。现有的 `BuildPlugins` Target 通过通配符 `..\Plugins\*\*.csproj` 自动包含新插件项目。

---

## 10. 性能优化措施

### 10.1 资源占用控制

| 优化项 | 措施 | 目标 |
|--------|------|------|
| 模型内存 | CTranslate2 INT8 量化 | 模型内存 ~300 MB |
| 子进程内存 | Python 进程 RSS 上限监控 | 超过 1.5 GB 时重启子进程 |
| GPU 显存 | 可选 GPU 推理（Vulkan/CUDA） | 与 Sherpa-Ncnn 共享 GPU 时限制显存 |
| 翻译缓存 | LRU 缓存最近 50 条翻译结果 | 避免重复翻译相同文本 |

### 10.2 线程与并发

| 优化项 | 措施 |
|--------|------|
| 子进程管理 | 独立后台线程监听 stdout，避免阻塞主线程 |
| 翻译请求队列 | `Channel<string>` 无锁队列，生产者-消费者模式 |
| 取消机制 | `CancellationToken` 支持翻译请求取消（如停止识别时） |
| 结果分发 | `Dispatcher.Invoke` / `RxApp.MainThreadScheduler` 确保 UI 更新在主线程 |

### 10.3 冷启动优化

| 优化项 | 措施 |
|--------|------|
| 子进程预启动 | 翻译功能启用时即启动子进程，不等首次翻译请求 |
| 模型预热 | 子进程启动后执行一条空翻译，预加载模型到内存 |
| 健康检查 | 主程序定期 ping 子进程（每 30s），超时则自动重启 |

---

## 11. 错误处理流程

### 11.1 错误分类与处理

| 错误场景 | 检测方式 | 处理策略 | 用户通知 |
|----------|----------|----------|----------|
| 翻译进程启动失败 | Process.Start 返回 null / 异常 | 3 次重试，间隔 2s；失败后降级为仅原文模式 | 通知栏错误提示 |
| 翻译进程崩溃退出 | stdout 管道断开 / Exit 事件 | 自动重启（最多 3 次/10 分钟） | 通知栏警告 |
| 翻译超时（> 5s） | 请求 ID + 计时器 | 标记该请求失败，不阻塞后续翻译 | 无（静默降级） |
| 模型文件缺失 | 文件存在性检查 | 提示用户下载模型 | 弹窗提示 + 跳转资源管理器 |
| Python 环境缺失 | python --version 检测 | 提示用户安装 Python 3.8+ | 弹窗提示 |
| 翻译结果为空 | 输出 JSON text 为空 | 忽略该结果，保留上次翻译 | 无 |
| 内存超限 | 子进程 RSS 监控 | 重启子进程 | 通知栏警告 |

### 11.2 降级策略

```
正常模式：原文 + 译文双语展示
    ↓ (翻译进程异常)
降级模式：仅原文展示，翻译功能标记为不可用
    ↓ (翻译进程恢复)
自动恢复：重新启用双语展示
```

- 降级和恢复均为自动进行，无需用户手动操作
- 连续降级 3 次后，自动禁用翻译功能并提示用户检查配置

### 11.3 日志记录

| 日志级别 | 内容 | 输出位置 |
|----------|------|----------|
| INFO | 翻译请求/结果摘要 | 翻译日志文件（可配置路径） |
| WARNING | 翻译超时、进程重启 | 主程序日志 + 通知栏 |
| ERROR | 进程崩溃、模型加载失败 | 主程序日志 + 通知栏 |
| DEBUG | 完整的请求/响应 JSON | 仅 Debug 模式 |

---

## 12. 测试策略

### 12.1 单元测试

| 测试目标 | 测试内容 |
|----------|----------|
| TranslatorWorkerClient | JSON 协议序列化/反序列化、进程管理、超时处理 |
| TranslationContext | 上下文队列的入队、出队、容量限制 |
| 翻译节流 | 200ms 内重复请求的过滤逻辑 |
| 配置序列化 | TranslatorConfigTypes 的序列化/反序列化 |

### 12.2 集成测试

| 测试场景 | 验证要点 |
|----------|----------|
| 完整流程 | AudioSource → Recognizer → JobManager → Translator → ViewModel → View |
| 翻译进程生命周期 | 启动、运行、崩溃恢复、关闭 |
| 上下文传递 | 多轮对话翻译的上下文拼接正确性 |
| 配置切换 | 运行中切换翻译器/模型的平滑过渡 |

### 12.3 性能测试

| 指标 | 测试方法 | 合格标准 |
|------|----------|----------|
| 翻译延迟 | 50 句英文（5-30 词）的端到端延迟 | P95 < 1.5s |
| 临时翻译延迟 | 10 次连续 TextChanged 的翻译延迟 | P95 < 0.8s |
| 内存增量 | 翻译功能开启前后的 RSS 差值 | < 1.5 GB |
| CPU 占用 | 翻译推理期间的 CPU 使用率 | < 30%（单核） |
| 翻译质量 | 100 句 WMT 测试集的 BLEU 分数 | ≥ 28 |

---

## 13. 实施计划

| 阶段 | 内容 | 预估工期 |
|------|------|----------|
| **P0: 基础设施** | ITranslator 接口扩展、TranslatorConfigTypes、PluginManager 适配 | 3 天 |
| **P1: 翻译引擎** | translator_worker.py、CTranslate2 集成、JSON 协议实现 | 5 天 |
| **P2: 插件开发** | LocalEnZhTranslator、TranslatorWorkerClient、ConfigEditor | 5 天 |
| **P3: 调度层** | JobManager 翻译调度、上下文管理、节流逻辑 | 3 天 |
| **P4: 界面改造** | CaptionView 双语布局、ConfigWindow 翻译标签页、HistoryView、TrayMenu | 4 天 |
| **P5: 模型集成** | ResourceManager 适配 translator_model 类型、模型下载流程 | 2 天 |
| **P6: 测试优化** | 单元测试、集成测试、性能调优、错误处理完善 | 4 天 |
| **合计** | | **约 26 人天** |

---

## 附录 A：文件变更清单

### 新增文件

| 文件路径 | 说明 |
|----------|------|
| `src/Plugins/TMSpeech.Translator.LocalEnZh/TMSpeech.Translator.LocalEnZh.csproj` | 翻译插件项目文件 |
| `src/Plugins/TMSpeech.Translator.LocalEnZh/LocalEnZhTranslator.cs` | ITranslator 实现 |
| `src/Plugins/TMSpeech.Translator.LocalEnZh/LocalEnZhTranslatorConfigEditor.cs` | 配置编辑器 |
| `src/Plugins/TMSpeech.Translator.LocalEnZh/TranslatorWorkerClient.cs` | 子进程管理客户端 |
| `src/Plugins/TMSpeech.Translator.LocalEnZh/tmmodule.json` | 模块元数据 |
| `src/Plugins/TMSpeech.Translator.LocalEnZh/worker/translator_worker.py` | Python 翻译推理进程 |
| `src/Plugins/TMSpeech.Translator.LocalEnZh/worker/requirements.txt` | Python 依赖 |

### 修改文件

| 文件路径 | 变更内容 |
|----------|----------|
| `src/TMSpeech.Core/Plugins/ITranslator.cs` | 扩展接口：新增 `TranslateAsync`、`SetContext`、`TranslationCompleted` 事件 |
| `src/TMSpeech.Core/JobManager.cs` | 新增翻译调度逻辑、`TranslationCompleted` 事件、上下文管理 |
| `src/TMSpeech.Core/ConfigTypes.cs` | 新增 `TranslatorConfigTypes` |
| `src/TMSpeech.GUI/ViewModels/MainViewModel.cs` | 新增 `TranslatedText`、翻译事件订阅 |
| `src/TMSpeech.GUI/Views/MainWindow.axaml` | CaptionView 绑定译文属性 |
| `src/TMSpeech.GUI/Controls/CaptionView.axaml` | 新增译文 TextBlock |
| `src/TMSpeech.GUI/Controls/CaptionView.axaml.cs` | 新增 `TranslatedText` 等依赖属性 |
| `src/TMSpeech.GUI/ViewModels/ConfigViewModel.cs` | 新增 `TranslatorSectionConfigViewModel` |
| `src/TMSpeech.GUI/Views/ConfigWindow.axaml` | 新增翻译标签页 |
| `src/TMSpeech.GUI/Controls/HistoryView.axaml` | 历史条目增加译文行 |
| `src/TMSpeech.GUI/Controls/TrayMenu.cs` | 新增翻译相关菜单项 |

## 附录 B：Python Worker 依赖

```
# requirements.txt
ctranslate2>=4.0
```

模型需预先使用 `ct2-opennmt-tf-converter` 或 `ct2-hf-converter` 将 OPUS-MT 模型转换为 CTranslate2 格式。
