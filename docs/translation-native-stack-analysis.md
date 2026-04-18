# 源技术栈翻译能力支持分析

> 日期：2026-04-18
> 项目：TMSpeech

---

## 1. 现有技术栈构成

### 1.1 识别器引擎

| 插件 | NuGet 包 | 底层引擎 | C# API 形态 |
|------|----------|----------|-------------|
| SherpaOnnx | `org.k2fsa.sherpa.onnx` 1.12.19 | ONNX Runtime（内嵌） | **封装的高层 API**：`OnlineRecognizer`、`OnlineStream`、`OnlineRecognizerConfig` |
| SherpaNcnn | `org.k2fsa.sherpa.ncnn` 2.1.13 | NCNN + Vulkan | **封装的高层 API**：`OnlineRecognizer`、`OnlineStream`、`OnlineRecognizerConfig` |

### 1.2 关键事实：sherpa-onnx 对 ONNX Runtime 的封装方式

```
org.k2fsa.sherpa.onnx NuGet 包结构
├── lib/
│   └── net6.0/
│       └── SherpaOnnx.dll          ← C# 绑定层，仅暴露语音识别 API
└── runtimes/
    └── win-x64/
        └── native/
            ├── sherpa-onnx-c-api.dll  ← C 语言中间层
            ├── onnxruntime.dll        ← 内嵌的 ONNX Runtime
            └── ...其他 native 库
```

**核心问题**：`SherpaOnnx.dll` 只暴露了语音识别相关的高层类（`OnlineRecognizer`、`OnlineStream` 等），**不暴露底层的 `InferenceSession`、`NamedOnnxValue` 等 ONNX Runtime API**。虽然 `onnxruntime.dll` 确实存在于运行时目录中，但：

1. 它被 `sherpa-onnx-c-api.dll` 内部独占加载
2. C# 层无法直接获取 `InferenceSession` 实例
3. 无法用 sherpa-onnx 的 `OnlineRecognizer` 来执行翻译任务

### 1.3 现有代码中的 API 使用模式

```csharp
// SherpaOnnx 插件 — 完全使用封装后的高层 API
var config = new OnlineRecognizerConfig();       // sherpa-onnx 专属配置
config.ModelConfig.Transducer.Encoder = encoder;  // 只支持 Transducer 模型
recognizer = new OnlineRecognizer(config);        // 只能做语音识别
stream = recognizer.CreateStream();
recognizer.Decode(stream);
var text = recognizer.GetResult(stream).Text;
```

这些 API **全部是语音识别专用的**，不存在任何通用的 ONNX 模型推理入口。

---

## 2. 逐项技术支持评估

### 2.1 能否复用 sherpa-onnx 的 ONNX Runtime 实例？

**不能。**

| 原因 | 说明 |
|------|------|
| 封装隔离 | `onnxruntime.dll` 由 `sherpa-onnx-c-api.dll` 内部管理，C# 层无法拿到 OrtEnv / InferenceSession 句柄 |
| 加载冲突 | 如果翻译插件另外引用 `Microsoft.ML.OnnxRuntime`，会尝试加载第二个 `onnxruntime.dll`，可能导致版本冲突或双重加载 |
| 模型格式不兼容 | sherpa-onnx 的 `OnlineRecognizer` 只接受 Transducer/Conformer 等语音模型架构，无法加载 Seq2Seq 翻译模型 |

### 2.2 能否直接引用 `Microsoft.ML.OnnxRuntime` 并与 sherpa-onnx 共存？

**可以，但需注意版本对齐和 native 库加载。**

| 议题 | 分析 |
|------|------|
| **版本冲突** | sherpa-onnx 内嵌了特定版本的 `onnxruntime.dll`（~1.14-1.16），如果 `Microsoft.ML.OnnxRuntime` NuGet 引入的版本不同，两个 DLL 在运行时目录中会产生冲突 |
| **native 加载** | `PluginLoadContext` 会从插件的 `runtimes/win-x64/native/` 目录加载 unmanaged DLL。如果两个插件目录各有一个不同版本的 `onnxruntime.dll`，加载顺序决定哪个生效 |
| **进程级共享** | Windows 上 DLL 按文件名全局共享。一旦某个 `onnxruntime.dll` 被加载，后续同名的 DLL 请求会复用已加载的实例 |

**实际风险**：如果版本不匹配，可能引发以下问题：
- API 符号找不到（新版有旧版无）
- ABI 不兼容导致 AccessViolation
- 功能特性不一致

### 2.3 sherpa-onnx/ncnn 是否支持翻译模型？

**不支持。**

sherpa-onnx 的 `OnlineRecognizer` 专门针对 **流式语音识别** 设计：
- 只支持 Transducer / Conformer / CTC 等语音架构
- 使用 `AcceptWaveform()` 接收音频，`GetResult()` 输出文本
- 没有 Seq2Seq / Encoder-Decoder 翻译推理能力
- 不支持自回归解码（beam search / sampling）

sherpa-ncnn 同理，专用于语音识别。

---

## 3. 结论：现有技术栈的翻译支持能力

| 能力维度 | 支持情况 | 说明 |
|----------|----------|------|
| ONNX Runtime 存在 | ✓ 存在 | 由 sherpa-onnx 包内嵌，版本 1.14+ |
| ONNX Runtime 可直接调用 | ✗ 不可用 | 被封装在 sherpa-onnx C API 内部，无法从 C# 层直接使用 |
| 识别 API 做翻译 | ✗ 不可能 | OnlineRecognizer 只支持语音模型架构 |
| 模型格式兼容 | ✗ 不兼容 | 翻译模型（MarianNMT/Seq2Seq）与语音模型完全不同 |
| native 库加载机制可复用 | ✓ 可复用 | `PluginLoadContext.LoadUnmanagedDll` 的 `runtimes/{rid}/native/` 路径发现机制可以直接复用 |

**一句话总结**：现有技术栈的 ONNX Runtime **物理存在但逻辑不可达**，无法直接用于翻译推理。需要引入独立的 ONNX Runtime 依赖。

---

## 4. 修正后的技术选型建议

### 4.1 方案对比（修正版）

既然无法复用 sherpa-onnx 的 ONNX Runtime 实例，"零新增依赖"的优势不复存在。重新评估：

| # | 方案 | 新增运行时依赖 | 集成复杂度 | 推理速度 | 翻译质量 | 模型大小 | 部署难度 |
|---|------|---------------|-----------|---------|---------|---------|---------|
| A | Microsoft.ML.OnnxRuntime + OPUS-MT | onnxruntime.dll（可能与现有版冲突） | 中 | ★★★★ | ★★★★ | ~150-500MB | 中 |
| B | CTranslate2 + Python 子进程 | Python + ctranslate2 | 高 | ★★★★★ | ★★★★ | ~100MB | 高 |
| C | Bergamot Translator | bergamot DLL | 中 | ★★★★ | ★★★ | ~80MB | 中 |
| D | 自编译 sherpa-onnx 翻译扩展 | 无（复用现有 sherpa-onnx） | 极高 | ★★★★ | ★★★★ | ~150MB | 低 |
| E | llama.cpp + 小型 LLM | llama.dll | 高 | ★★ | ★★★★★ | ~400MB+ | 高 |
| **F** | **独立 CTranslate2 C 库 + P/Invoke** | **ct2 DLL** | **中** | **★★★★★** | **★★★★** | **~100MB** | **中** |

### 4.2 新增方案 F：CTranslate2 C 库 + P/Invoke

CTranslate2 官方提供 **C API** 和预编译的共享库：

```
ctranslate2/
├── include/ctranslate2/
│   └── ctranslate2.h     ← C API 头文件
└── lib/
    └── ctranslate2.dll   ← 预编译 Windows x64 DLL
```

C API 核心接口：

```c
// 创建翻译器
ctranslate2_translator_t* ctranslate2_translator_new(const char* model_path, int device);

// 翻译
ctranslate2_translation_result_t* ctranslate2_translator_translate(
    ctranslate2_translator_t* translator,
    size_t num_source,
    const char** source,
    const ctranslate2_translation_options_t* options
);

// 释放
void ctranslate2_translation_result_delete(ctranslate2_translation_result_t* result);
void ctranslate2_translator_delete(ctranslate2_translator_t* translator);
```

C# P/Invoke 调用：

```csharp
[DllImport("ctranslate2.dll", CallingConvention = CallingConvention.Cdecl)]
private static extern IntPtr ctranslate2_translator_new(
    [MarshalAs(UnmanagedType.LPStr)] string modelPath, int device);

[DllImport("ctranslate2.dll", CallingConvention = CallingConvention.Cdecl)]
private static extern void ctranslate2_translator_delete(IntPtr translator);

// ...其他 P/Invoke 声明
```

**方案 F 的优势**：

| 维度 | 评估 |
|------|------|
| 无 Python 依赖 | ✓ 纯 native DLL，随插件分发 |
| 进程内调用 | ✓ P/Invoke，无子进程管理 |
| 推理速度最快 | ✓ CTranslate2 的 CPU INT8 推理是目前最快的 |
| 模型体积小 | ✓ CT2 格式 INT8 ~100MB |
| 上下文支持 | ✓ C API 支持 `num_source` 参数传入多行上下文 |
| 与现有架构一致 | ✓ 与 sherpa-onnx 的 P/Invoke 模式相同，通过 `PluginLoadContext.LoadUnmanagedDll` 加载 |

### 4.3 修正后的推荐排序

| 排序 | 方案 | 理由 |
|------|------|------|
| **1** | **F: CTranslate2 C 库 + P/Invoke** | 无 Python 依赖 + 进程内调用 + 推理最快 + 架构一致性最好 |
| **2** | A: Microsoft.ML.OnnxRuntime + OPUS-MT | 可行但需处理 onnxruntime.dll 版本冲突，且 ORT 做 Seq2Seq 自回归解码需要手写解码循环 |
| **3** | D: 自编译 sherpa-onnx 翻译扩展 | 理论上可行但工程量极大，需 fork sherpa-onnx 并添加翻译 API |
| **4** | C: Bergamot Translator | 模型小速度快，但英中翻译质量不足 |
| **5** | B: CTranslate2 + Python 子进程 | 需 Python 环境，违背开箱即用 |
| **6** | E: llama.cpp + 小型 LLM | 资源消耗过大，不适合实时场景 |

### 4.4 方案 A vs 方案 F 深度对比

| 对比维度 | A: ORT + OPUS-MT | F: CT2 C 库 + P/Invoke |
|----------|------------------|------------------------|
| **新增 DLL** | `onnxruntime.dll`（可能与 sherpa-onnx 的冲突） | `ctranslate2.dll` + 依赖（无冲突） |
| **DLL 冲突风险** | **高** — 两个不同版本的 onnxruntime.dll | **无** — ctranslate2.dll 是独立的 |
| **自回归解码** | 需手写 decoder 循环 + KV Cache 管理 | C API 内置，一次调用完成 |
| **INT8 量化** | 需额外用 Python 工具量化 | CT2 模型原生支持，下载即用 |
| **beam search** | 需手写或在 ORT 中手动实现 | C API 内置 `beam_size` 参数 |
| **代码量** | 大（分词 + 编码 + 解码循环 + KV Cache + beam） | 小（P/Invoke 声明 + 几个调用） |
| **社区参考** | ORT 做 MarianNMT 翻译的 C# 示例极少 | CT2 C API 有官方示例 |
| **维护成本** | 高（自回归解码逻辑复杂，易出 bug） | 低（P/Invoke 声明 + 封装类） |

**关键差异**：方案 A 需要手写 Seq2Seq 的自回归解码循环（逐 token 调用 decoder、管理 past key values、实现 beam search），这是 MarianNMT 模型在 ONNX Runtime 上推理的核心难点。而方案 F 的 CTranslate2 C API 一步 `translate()` 调用就完成了所有这些。

### 4.5 方案 F 的技术风险

| 风险 | 严重度 | 缓解措施 |
|------|--------|----------|
| CT2 C API 的 C# 绑定需手写 P/Invoke | 中 | API 接口少（~20 个函数），且有官方 C 头文件可参照 |
| CT2 DLL 体积较大（~50MB+依赖） | 低 | 通过资源管理器下载，随模型一起分发 |
| CT2 版本更新可能破坏 C API ABI | 低 | 锁定 CT2 版本，随插件版本一起发布 |
| 缺少现成的 C# CT2 绑定库 | 中 | 需自行编写 P/Invoke 声明和封装类，但工作量可控 |

---

## 5. 方案 F 实施要点

### 5.1 CTranslate2 C API 核心 P/Invoke 声明

```csharp
// TranslatorNativeMethods.cs
internal static class Ct2NativeMethods
{
    private const string DllName = "ctranslate2";

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr ctranslate2_translator_new(
        [MarshalAs(UnmanagedType.LPStr)] string model_path,
        int device,  // 0=CPU, 1=CUDA
        IntPtr compute_type,  // null for default
        IntPtr device_indices,
        uint num_device_indices,
        IntPtr options  // null for default
    );

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ctranslate2_translator_delete(IntPtr translator);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr ctranslate2_translator_translate(
        IntPtr translator,
        UIntPtr num_source,
        IntPtr source,  // const char* const*
        UIntPtr num_target_prefix,
        IntPtr target_prefix,
        IntPtr options
    );

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern UIntPtr ctranslate2_translation_result_get_num_translations(
        IntPtr result, UIntPtr hypothesis_index
    );

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr ctranslate2_translation_result_get_translation(
        IntPtr result, UIntPtr hypothesis_index, UIntPtr token_index
    );

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ctranslate2_translation_result_delete(IntPtr result);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr ctranslate2_translation_options_new();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ctranslate2_translation_options_delete(IntPtr options);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ctranslate2_translation_options_set_beam_size(
        IntPtr options, UIntPtr beam_size
    );

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ctranslate2_translation_options_set_max_decoding_length(
        IntPtr options, UIntPtr length
    );
}
```

### 5.2 翻译器封装类

```csharp
// Ct2Translator.cs
public class Ct2Translator : IDisposable
{
    private IntPtr _translator = IntPtr.Zero;

    public void LoadModel(string modelPath)
    {
        _translator = Ct2NativeMethods.ctranslate2_translator_new(
            modelPath, device: 0,  // CPU
            compute_type: IntPtr.Zero,
            device_indices: IntPtr.Zero,
            num_device_indices: 0,
            options: IntPtr.Zero
        );
        if (_translator == IntPtr.Zero)
            throw new InvalidOperationException("Failed to create CT2 translator");
    }

    public string Translate(string text, int beamSize = 4)
    {
        // 构造 source 数组（分词后的 token 列表）
        var sourceTokens = Tokenize(text);
        var sourceHandles = sourceTokens
            .Select(s => Marshal.StringToHGlobalAnsi(s))
            .ToArray();
        var sourcePtr = Marshal.AllocHGlobal(sourceHandles.Length * IntPtr.Size);
        Marshal.Copy(sourceHandles, 0, sourcePtr, sourceHandles.Length);

        // 创建翻译选项
        var options = Ct2NativeMethods.ctranslate2_translation_options_new();
        Ct2NativeMethods.ctranslate2_translation_options_set_beam_size(
            options, (UIntPtr)beamSize);

        // 执行翻译
        var result = Ct2NativeMethods.ctranslate2_translator_translate(
            _translator,
            (UIntPtr)sourceHandles.Length,
            sourcePtr,
            num_target_prefix: UIntPtr.Zero,
            target_prefix: IntPtr.Zero,
            options
        );

        // 读取结果
        var translation = GetTranslationString(result, hypothesisIndex: 0);

        // 清理
        Ct2NativeMethods.ctranslate2_translation_result_delete(result);
        Ct2NativeMethods.ctranslate2_translation_options_delete(options);
        foreach (var h in sourceHandles) Marshal.FreeHGlobal(h);
        Marshal.FreeHGlobal(sourcePtr);

        return Detokenize(translation);
    }

    public void Dispose()
    {
        if (_translator != IntPtr.Zero)
        {
            Ct2NativeMethods.ctranslate2_translator_delete(_translator);
            _translator = IntPtr.Zero;
        }
    }
}
```

### 5.3 分词处理

OPUS-MT 模型使用 **SentencePiece** 分词。CTranslate2 C API 接收的是**已分词的 token 数组**（非原始文本字符串）。

两个方案：

| 方案 | 实现 | 优劣 |
|------|------|------|
| 在 C# 侧用 SentencePiece.NET 分词 | 引入 `SentencePiece` NuGet 包 | 额外依赖，但精确控制 |
| 利用 CT2 的 `translate_batch` 接受原始文本 | 某些 CT2 绑定支持传入原始文本 | 需确认 C API 是否支持 |

**推荐**：在 C# 侧用 `SentencePiece` NuGet 做分词/反分词，与翻译推理解耦。

### 5.4 上下文传递

CTranslate2 的 `translate_batch` C API 支持传入多行 source 作为前缀上下文：

```csharp
// 传入上下文 + 当前句子
var sourceLines = new List<string[]>();

// 上下文句对（source 端）
foreach (var ctx in contextItems)
    sourceLines.Add(Tokenize(ctx.SourceText));

// 当前待翻译句子
sourceLines.Add(Tokenize(currentText));

// 一次调用翻译所有行，CT2 内部会利用前序 source 作为上下文
var results = Ct2NativeMethods.ctranslate2_translator_translate_batch(...);
```

### 5.5 Native DLL 分发

```
src/Plugins/TMSpeech.Translator.LocalEnZh/
├── TMSpeech.Translator.LocalEnZh.csproj
├── LocalEnZhTranslator.cs
├── LocalEnZhTranslatorConfigEditor.cs
├── Ct2Translator.cs                 ← CT2 C API 封装
├── Ct2NativeMethods.cs              ← P/Invoke 声明
├── tmmodule.json
└── runtimes/
    └── win-x64/
        └── native/
            ├── ctranslate2.dll       ← CT2 预编译库
            ├── onnxruntime.dll       ← CT2 内部依赖（独立版本，不与 sherpa-onnx 冲突）
            └── ...
```

`PluginLoadContext` 已有的 `LoadUnmanagedDll` 逻辑会自动从此目录加载 native DLL，**无需额外适配**。

### 5.6 模型分发

通过现有 `ResourceManager` 系统下载，新增 `translator_model` 资源类型：

```json
{
    "id": "opus-mt-en-zh-ct2-int8",
    "name": "OPUS-MT 英中翻译模型 (CT2 INT8)",
    "type": "translator_model",
    "displayVersion": "1.0.0",
    "install": [
        { "type": "download", "url": "https://..." },
        { "type": "extract", "extractType": "zip", "extractTo": "." }
    ]
}
```

---

## 6. 需求文档修订对照

| 原文档章节 | 原方案 | 修订为 |
|-----------|--------|--------|
| 3.1 翻译引擎选型 | CTranslate2 + Python 子进程 | CTranslate2 C 库 + P/Invoke |
| 3.2 通信协议 | stdin/stdout JSON 行协议 | 进程内 P/Invoke 直接调用 |
| 3.3 翻译进程架构 | Python worker 子进程 | C# `Ct2Translator` 封装类 |
| 3.4 模型管理 | CT2 格式模型 | CT2 INT8 格式模型（不变） |
| 3.5 上下文连贯性 | Python 侧拼接 | C# 侧通过 `translate_batch` 传入多行 source |
| 3.6 配置编辑器 | 含 `python_path` | 移除 `python_path`，新增 `compute_type` |
| 9.1 新增项目 | 含 `worker/` Python 目录 | 移除 `worker/`，新增 `Ct2Translator.cs`、`Ct2NativeMethods.cs` |
| 9.2 csproj 配置 | 无 NuGet 依赖 | 新增 `SentencePiece` NuGet + `runtimes/` native DLL |
| 10 性能优化 | 子进程管理相关 | 删除子进程相关，保留 CT2 推理优化 |
| 11 错误处理 | Python 进程崩溃恢复 | 简化为 P/Invoke 异常捕获 + DLL 加载失败处理 |
