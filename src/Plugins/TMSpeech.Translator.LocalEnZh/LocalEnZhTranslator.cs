using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using TMSpeech.Core.Plugins;
using TMSpeech.Core.Services.Resource;

namespace TMSpeech.Translator.LocalEnZh;

public class LocalEnZhTranslator : ITranslator
{
    // IPlugin metadata
    public string GUID => "B3E4C5D6-7890-1234-ABCD-EF5678901234";
    public string Name => "本地英中翻译器";
    public string Description => "基于CTranslate2的离线英中翻译 (English→中文)";
    public string Version => "1.0.0";
    public string SupportVersion => "any";
    public string Author => "Built-in";
    public string Url => "";
    public string License => "MIT License";
    public string Note => "";

    public bool Available => _available;
    private bool _available = true;

    public IPluginConfigEditor CreateConfigEditor() => new LocalEnZhTranslatorConfigEditor();

    // Config
    private LocalEnZhConfig _config = new();

    // Translation engine
    private Ct2Translator? _ct2Translator;
    private bool _modelLoaded = false;
    private readonly object _translateLock = new();

    // Context
    private IReadOnlyList<TranslationContextItem> _context = new List<TranslationContextItem>();

    // Events
    public event EventHandler<TranslationEventArgs>? TranslationCompleted;
    public event EventHandler<Exception>? ExceptionOccured;

    public void LoadConfig(string config)
    {
        if (!string.IsNullOrEmpty(config))
        {
            try
            {
                _config = JsonSerializer.Deserialize<LocalEnZhConfig>(config) ?? new LocalEnZhConfig();
            }
            catch
            {
                _config = new LocalEnZhConfig();
            }
        }

        // Attempt to load model on config change
        TryLoadModel();
    }

    private void TryLoadModel()
    {
        try
        {
            lock (_translateLock)
            {
                _ct2Translator?.Dispose();
                _ct2Translator = null;
                _modelLoaded = false;

                string? modelPath = ResolveModelPath();
                if (string.IsNullOrEmpty(modelPath) || !Directory.Exists(modelPath))
                {
                    Debug.WriteLine($"[LocalEnZhTranslator] Model path not found: {modelPath}");
                    _available = false;
                    return;
                }

                _ct2Translator = new Ct2Translator();
                _ct2Translator.LoadModel(modelPath);
                _modelLoaded = true;
                _available = true;

                Debug.WriteLine($"[LocalEnZhTranslator] Model loaded from: {modelPath}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LocalEnZhTranslator] Failed to load model: {ex.Message}");
            _available = false;
            _modelLoaded = false;
            ExceptionOccured?.Invoke(this, new InvalidOperationException($"翻译模型加载失败: {ex.Message}", ex));
        }
    }

    private string? ResolveModelPath()
    {
        // Priority 1: Use resource manager model ID
        if (!string.IsNullOrEmpty(_config.Model))
        {
            try
            {
                var res = ResourceManagerFactory.Instance.GetLocalResource(_config.Model).Result;
                if (res != null)
                    return res.LocalDir;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LocalEnZhTranslator] Failed to resolve model from resource manager: {ex.Message}");
            }
        }

        // Priority 2: Custom model path
        if (!string.IsNullOrEmpty(_config.CustomModelPath) && Directory.Exists(_config.CustomModelPath))
            return _config.CustomModelPath;

        return null;
    }

    public void SetContext(IReadOnlyList<TranslationContextItem> context)
    {
        _context = context;
    }

    public void TranslateAsync(string text, bool isFinal)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        // Run translation in a background thread to avoid blocking
        Task.Run(() =>
        {
            try
            {
                var result = DoTranslate(text, isFinal);
                TranslationCompleted?.Invoke(this, new TranslationEventArgs
                {
                    OriginalText = text,
                    TranslatedText = result,
                    IsFinal = isFinal
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LocalEnZhTranslator] Translation failed: {ex.Message}");
                ExceptionOccured?.Invoke(this, ex);
            }
        });
    }

    public string Translate(string text)
    {
        return DoTranslate(text, isFinal: true);
    }

    private string DoTranslate(string text, bool isFinal)
    {
        if (!_modelLoaded || _ct2Translator == null)
            return "";

        lock (_translateLock)
        {
            if (!_modelLoaded || _ct2Translator == null)
                return "";

            try
            {
                // Tokenize
                var sourceTokens = SimpleTokenizer.Tokenize(text);

                // Build context batches
                var batches = new List<List<string>>();
                foreach (var ctx in _context)
                {
                    batches.Add(SimpleTokenizer.Tokenize(ctx.SourceText));
                }
                batches.Add(sourceTokens);

                var beamSize = isFinal ? _config.BeamSize : 1;  // Greedy for partial, beam for final

                // Translate
                List<string> resultTokens;
                if (batches.Count > 1)
                {
                    resultTokens = _ct2Translator.TranslateBatchTokens(batches, beamSize, _config.MaxDecodingLength);
                }
                else
                {
                    resultTokens = _ct2Translator.TranslateTokens(sourceTokens, beamSize, _config.MaxDecodingLength);
                }

                // Detokenize
                var result = SimpleTokenizer.Detokenize(resultTokens);

                // Post-processing
                result = PostProcess(result, text);

                return result;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LocalEnZhTranslator] DoTranslate error: {ex.Message}");
                return "";
            }
        }
    }

    private static string PostProcess(string translation, string original)
    {
        if (string.IsNullOrWhiteSpace(translation))
            return "";

        // If translation is identical to original (e.g., proper nouns), return empty to avoid duplication
        if (translation.Trim() == original.Trim())
            return "";

        // Ensure Chinese punctuation
        translation = translation
            .Replace(",", "，")
            .Replace(".", "。")
            .Replace("!", "！")
            .Replace("?", "？")
            .Replace(":", "：")
            .Replace(";", "；")
            .Replace("(", "（")
            .Replace(")", "）");

        // Fix consecutive punctuation
        while (translation.Contains("。。")) translation = translation.Replace("。。", "。");
        while (translation.Contains("，。")) translation = translation.Replace("，。", "。");

        return translation.Trim();
    }

    public void Init()
    {
        Debug.WriteLine("[LocalEnZhTranslator] Init");
    }

    public void Destroy()
    {
        lock (_translateLock)
        {
            _ct2Translator?.Dispose();
            _ct2Translator = null;
            _modelLoaded = false;
        }
    }
}

internal class LocalEnZhConfig
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("custom_model_path")]
    public string CustomModelPath { get; set; } = "";

    [JsonPropertyName("beam_size")]
    public int BeamSize { get; set; } = 4;

    [JsonPropertyName("max_decoding_length")]
    public int MaxDecodingLength { get; set; } = 256;
}
