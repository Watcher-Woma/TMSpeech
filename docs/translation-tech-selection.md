# 翻译服务技术选型分析

> 日期：2026-04-18
> 项目：TMSpeech

---

## 1. 需求文档选型的问题

需求文档中选了 **CTranslate2 + Python 子进程**，理由是"Python API 成熟、与 CommandRecognizer 架构一致"。但这个选择有明显缺陷：

| 问题 | 影响 |
|------|------|
| 引入 Python 运行时依赖 | 用户需安装 Python 3.8+，违背"开箱即用" |
| 子进程通信开销 | stdin/stdout 管道序列化/反序列化增加延迟 |
| 部署复杂度 | 需打包 Python worker + requirements.txt，pip install |
| 进程管理负担 | 崩溃重启、健康检查、内存监控等大量容错代码 |
| 与现有生态割裂 | 项目已用 `org.k2fsa.sherpa.onnx`（ONNX Runtime），另起 CTranslate2 技术栈重复 |

**根本原因**：过度参照了 `CommandRecognizer` 的"外部进程"模式，但 CommandRecognizer 是为了对接**用户自定义**的任意命令行工具，翻译器则是**项目自己控制**的内置功能，两者的设计约束完全不同。

---

## 2. 可行方案全面对比

### 2.1 方案总览

| # | 方案 | 推理引擎 | 集成方式 | 模型 |
|---|------|----------|----------|------|
| A | **ONNX Runtime + OPUS-MT** | ONNX Runtime | NuGet 包，进程内调用 | opus-mt-en-zh (ONNX 格式) |
| B | CTranslate2 + Python 子进程 | CTranslate2 | Python 子进程 + JSON 协议 | opus-mt-en-zh (CT2 格式) |
| C | Bergamot Translator | Bergamot (marian + WASM) | P/Invoke native 库 | bergamot 小模型 |
| D | ONNX Runtime + CT2 转换模型 | ONNX Runtime | NuGet 包，进程内调用 | CT2 量化后的 ONNX 模型 |
| E | LLaMA.cpp / GGUF 小模型 | llama.cpp | P/Invoke / 子进程 | Qwen2-0.5B-Instruct 等 |

### 2.2 逐项详细对比

#### A. ONNX Runtime + OPUS-MT（推荐）

```
集成方式：Microsoft.ML.OnnxRuntime NuGet → 进程内 C# 调用
模型：Helsinki-NLP/opus-mt-en-zh 导出为 ONNX 格式
```

| 维度 | 评估 |
|------|------|
| 生态一致性 | ★★★★★ 项目已依赖 `org.k2fsa.sherpa.onnx`（内含 ONNX Runtime），零新增运行时依赖 |
| 集成复杂度 | ★★★★★ NuGet 包直接调用，无外部进程、无 Python |
| 部署体验 | ★★★★★ 模型文件随资源管理器下载，无需用户安装任何额外软件 |
| 推理速度 | ★★★★☆ CPU 推理短句 200-500ms，支持 INT8 量化（OnnxRuntime QDQ） |
| 翻译质量 | ★★★★☆ OPUS-MT 英中 BLEU ~30，足够好 |
| 模型大小 | ★★★☆☆ ~300-500 MB（FP32），INT8 量化后 ~150 MB |
| 上下文支持 | ★★★☆☆ 需手动拼接上下文到 encoder 输入 |
| GPU 支持 | ★★★★☆ OnnxRuntime 原生支持 CUDA / DirectML |
| 内存占用 | ★★★★☆ 进程内共享，无子进程额外开销 |

**关键优势**：项目已通过 `org.k2fsa.sherpa.onnx` 包引入了 ONNX Runtime 的 native 库。`PluginLoadContext` 已经有完整的 `runtimes/win-x64/native/` 加载路径支持。翻译插件只需引用 `Microsoft.ML.OnnxRuntime` NuGet，native 库的加载路径与 SherpaOnnx 插件完全一致，无需任何额外适配。

```
SherpaOnnx 插件:  org.k2fsa.sherpa.onnx (NuGet) → ONNX Runtime → 推理
翻译插件:         Microsoft.ML.OnnxRuntime (NuGet) → ONNX Runtime → 推理
                                      ↑ 同一引擎
```

#### B. CTranslate2 + Python 子进程（需求文档中的方案）

| 维度 | 评估 |
|------|------|
| 生态一致性 | ★★☆☆☆ 引入全新推理引擎，与 ONNX Runtime 生态无关 |
| 集成复杂度 | ★★☆☆☆ 需管理 Python 进程生命周期、JSON 协议、崩溃恢复 |
| 部署体验 | ★☆☆☆☆ 用户需安装 Python + pip install ctranslate2 |
| 推理速度 | ★★★★★ CTranslate2 的 CPU 推理是当前最快的（INT8 优化极好） |
| 翻译质量 | ★★★★☆ 同 OPUS-MT，质量相同 |
| 模型大小 | ★★★★☆ CT2 格式 INT8 量化后 ~100 MB，最紧凑 |
| 上下文支持 | ★★★★☆ CTranslate2 API 原生支持前缀上下文 |
| GPU 支持 | ★★★★☆ 支持 CUDA |
| 内存占用 | ★★☆☆☆ Python 进程基础 ~80MB + 模型 ~300MB，总计 ~400MB+ |

#### C. Bergamot Translator

| 维度 | 评估 |
|------|------|
| 生态一致性 | ★★☆☆☆ 独立引擎 |
| 集成复杂度 | ★★★☆☆ 需 P/Invoke 调用 C++ bergamot 库，或子进程 |
| 部署体验 | ★★★☆☆ 需打包 native DLL |
| 推理速度 | ★★★★☆ 专为浏览器优化，短句极快（<200ms） |
| 翻译质量 | ★★★☆☆ 小模型（~80MB），英中质量不如 OPUS-MT |
| 模型大小 | ★★★★★ ~80MB，最小 |
| 上下文支持 | ★★☆☆☆ 有限 |
| GPU 支持 | ★☆☆☆☆ 仅 CPU |
| 内存占用 | ★★★★★ 最低 |

#### D. ONNX Runtime + CT2 转换模型

| 维度 | 评估 |
|------|------|
| 生态一致性 | ★★★★★ 与方案 A 相同，复用 ONNX Runtime |
| 集成复杂度 | ★★★★☆ NuGet 包调用 |
| 部署体验 | ★★★★★ 与方案 A 相同 |
| 推理速度 | ★★★★☆ CT2 量化模型在 ORT 上推理也快 |
| 翻译质量 | ★★★★☆ 与方案 B 相同 |
| 模型大小 | ★★★★☆ INT8 量化后 ~100-150 MB |
| 上下文支持 | ★★★☆☆ 需手动拼接 |
| GPU 支持 | ★★★★☆ ORT 支持 |
| 内存占用 | ★★★★☆ 进程内 |

#### E. LLaMA.cpp / 小型 LLM

| 维度 | 评估 |
|------|------|
| 生态一致性 | ★★☆☆☆ 新引擎 |
| 集成复杂度 | ★★☆☆☆ P/Invoke 或子进程 |
| 部署体验 | ★★☆☆☆ 模型较大 |
| 推理速度 | ★★☆☆☆ 0.5B 模型单句也需 1-3s（CPU） |
| 翻译质量 | ★★★★★ 上下文理解最好，意译能力强 |
| 模型大小 | ★★☆☆☆ GGUF Q4 量化 0.5B ~400MB，1.5B ~1GB |
| 上下文支持 | ★★★★★ 天然多轮对话支持 |
| GPU 支持 | ★★★★☆ llama.cpp 支持 Vulkan/CUDA |
| 内存占用 | ★☆☆☆☆ 最差 |

---

## 3. 评分汇总

| 维度 | A: ORT+OPUS-MT | B: CT2+Python | C: Bergamot | D: ORT+CT2模型 | E: LLaMA.cpp |
|------|:---:|:---:|:---:|:---:|:---:|
| 生态一致性 | 5 | 2 | 2 | 5 | 2 |
| 集成复杂度 | 5 | 2 | 3 | 4 | 2 |
| 部署体验 | 5 | 1 | 3 | 5 | 2 |
| 推理速度 | 4 | 5 | 4 | 4 | 2 |
| 翻译质量 | 4 | 4 | 3 | 4 | 5 |
| 模型大小 | 3 | 4 | 5 | 4 | 2 |
| 上下文支持 | 3 | 4 | 2 | 3 | 5 |
| GPU 支持 | 4 | 4 | 1 | 4 | 4 |
| 内存占用 | 4 | 2 | 5 | 4 | 1 |
| **总分** | **37** | **28** | **28** | **37** | **25** |

---

## 4. 最终推荐

### 首选：方案 A（ONNX Runtime + OPUS-MT）

理由：

1. **零新增运行时依赖** — 项目已通过 `org.k2fsa.sherpa.onnx` 引入了 ONNX Runtime native 库，翻译插件复用同一推理引擎，用户无需安装任何额外软件
2. **进程内调用** — 无子进程管理开销，延迟更低，代码更简洁
3. **生态统一** — 模型管理、native 库加载路径、GPU 加速方案全部与现有 SherpaOnnx 插件对齐
4. **开箱即用** — 用户下载安装后即可使用，无需 Python 环境

### 备选：方案 D（ONNX Runtime + CT2 量化模型）

如果对模型体积敏感（如需要通过资源管理器下载），可先用 `ct2-opennmt-tf-converter` 将 OPUS-MT 转为 CT2 INT8 格式，再导出为 ONNX。这样既复用 ORT 推理，又获得更小的模型文件。

### 方案 B 适用的唯一场景

如果未来需要支持**多语言对**（不止英中），CTranslate2 的多语言模型统一管理更方便。但就当前"英中翻译"单一需求而言，方案 A 足够。

---

## 5. 方案 A 技术要点

### 5.1 模型获取

```bash
# 从 HuggingFace 下载 OPUS-MT 英中模型并导出为 ONNX
pip install optimum[onnxruntime]
python -c "
from optimum.onnxruntime import ORTModelForSeq2SeqLM
model = ORTModelForSeq2SeqLM.from_pretrained('Helsinki-NLP/opus-mt-en-zh', export=True)
model.save_pretrained('./opus-mt-en-zh-onnx')
"
```

导出产物：
- `encoder_model.onnx` — 编码器
- `decoder_model.onnx` — 解码器（自回归）
- `decoder_with_past_model.onnx` — 带 KV Cache 的解码器（加速推理）
- `source_vocabulary.json` / `target_vocabulary.json` — 词表
- `config.json` — 模型配置

### 5.2 C# 推理核心逻辑（伪代码）

```csharp
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

public class OnnxTranslator
{
    private InferenceSession _encoderSession;
    private InferenceSession _decoderSession;
    private InferenceSession _decoderWithPastSession;
    private Tokenizer _tokenizer; // 基于 sentencepiece 或自定义分词

    public string Translate(string input)
    {
        // 1. 分词
        var inputIds = _tokenizer.Encode(input);
        
        // 2. Encoder 推理
        var encoderOutputs = _encoderSession.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("input_ids", ToTensor(inputIds))
        });
        var encoderHiddenState = encoderOutputs.First().AsTensor<long>();
        
        // 3. Decoder 自回归推理（带 KV Cache 加速）
        var (outputIds, pastKeyValues) = DecodeStep(
            encoderHiddenState, 
            initialInput: new[] { BOS_TOKEN },
            pastKeyValues: null);
        
        for (int i = 0; i < MAX_LENGTH; i++)
        {
            if (outputIds.Last() == EOS_TOKEN) break;
            (outputIds, pastKeyValues) = DecodeStep(
                encoderHiddenState,
                initialInput: new[] { outputIds.Last() },
                pastKeyValues: pastKeyValues);
        }
        
        // 4. 反分词
        return _tokenizer.Decode(outputIds);
    }
}
```

### 5.3 上下文拼接策略

OPUS-MT 基于 MarianNMT（Encoder-Decoder 架构），不原生支持多轮上下文。采用拼接策略：

```
输入格式："<s>前序原文1</s><t>前序译文1</t>...<s>当前英文</s>"
```

- 将上下文句对拼接到当前源文本前面，作为一个整体输入 encoder
- 最多拼接 3 轮上下文，超出则丢弃最早的
- 这种方式在 OPUS-MT 中已被验证有效，BLEU 可提升 1-2 分

### 5.4 INT8 量化（可选优化）

```python
# 使用 ONNX Runtime 量化工具
from onnxruntime.quantization import quantize_dynamic, QuantType

quantize_dynamic(
    model_input="encoder_model.onnx",
    model_output="encoder_model_int8.onnx",
    weight_type=QuantType.QInt8
)
# 对 decoder 和 decoder_with_past 同理
```

- 量化后模型体积缩小 2-3 倍（~150 MB）
- 推理速度提升 1.5-2 倍
- 翻译质量损失极小（BLEU 下降 < 0.5）

### 5.5 NuGet 依赖

```xml
<PackageReference Include="Microsoft.ML.OnnxRuntime" Version="1.17.0" />
```

- 该包自带 `runtimes/win-x64/native/onnxruntime.dll`
- 与 `org.k2fsa.sherpa.onnx` 包的 native 库加载路径机制完全兼容
- `PluginLoadContext` 已实现 `LoadUnmanagedDll` 从 `runtimes/{rid}/native/` 加载

### 5.6 与需求文档的差异

需求文档中基于方案 B 设计的以下内容需同步修订：

| 需求文档章节 | 修订内容 |
|-------------|---------|
| 3.1 翻译引擎选型 | 改为 ONNX Runtime + OPUS-MT（方案 A） |
| 3.2 通信协议 | 删除 JSON 行协议，改为进程内 C# 直接调用 |
| 3.3 翻译进程架构 | 删除 Python worker，改为 C# OnnxTranslator 类 |
| 3.4 模型管理 | 模型格式从 CT2 改为 ONNX，新增 `onnx_translation_model` 类型 |
| 3.5 上下文连贯性 | 拼接策略不变，但实现从 Python 侧移至 C# 侧 |
| 3.6 配置编辑器 | 删除 `python_path` 配置项，新增 `compute_type`（int8/fp32） |
| 9.1 新增项目 | 删除 `worker/` 目录，新增 `OnnxTranslator.cs` |
| 9.2 csproj 配置 | 依赖从无改为 `Microsoft.ML.OnnxRuntime` |
| 10 性能优化 | 删除子进程管理相关优化，新增 ORT SessionOptions 优化 |
| 11 错误处理 | 删除 Python 进程崩溃/重启相关处理，简化为 ORT 异常捕获 |
