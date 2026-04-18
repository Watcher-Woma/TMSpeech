using System.Collections.ObjectModel;
using System.Text.Json;
using TMSpeech.Core.Plugins;
using TMSpeech.Core.Services.Resource;

namespace TMSpeech.Translator.LocalEnZh;

public class LocalEnZhTranslatorConfigEditor : IPluginConfigEditor
{
    private LocalEnZhConfig _config = new();

    public event EventHandler? FormItemsUpdated;
    public event EventHandler? ValueUpdated;

    public IReadOnlyList<PluginConfigFormItem> GetFormItems()
    {
        var items = new List<PluginConfigFormItem>();

        // Model selection from resource manager
        var modelOptions = new Dictionary<object, string> { { "", "自定义路径" } };
        try
        {
            var resources = ResourceManagerFactory.Instance.GetAllResources().Result;
            var translatorModels = resources
                .Where(u => u.ModuleInfo?.Type == "translator_model")
                .ToList();

            foreach (var model in translatorModels)
            {
                modelOptions[model.ModuleInfo!.ID] = model.ModuleInfo.Name;
            }
        }
        catch { }

        items.Add(new PluginConfigFormItemOption(
            "model", "翻译模型",
            modelOptions,
            "从资源管理器下载的翻译模型"
        ));

        items.Add(new PluginConfigFormItemFolder(
            "custom_model_path", "自定义模型路径",
            "当未选择上方模型时，指定CTranslate2模型目录的本地路径"
        ));

        items.Add(new PluginConfigFormItemNumber(
            "beam_size", "Beam大小",
            "定稿翻译的搜索宽度，越大越准但越慢",
            Min: 1, Max: 8, IsInteger: true
        ));

        items.Add(new PluginConfigFormItemNumber(
            "max_decoding_length", "最大解码长度",
            "翻译输出的最大token数量",
            Min: 16, Max: 1024, IsInteger: true
        ));

        return items;
    }

    public IReadOnlyDictionary<string, object> GetAll()
    {
        return new Dictionary<string, object>
        {
            { "model", _config.Model },
            { "custom_model_path", _config.CustomModelPath },
            { "beam_size", _config.BeamSize },
            { "max_decoding_length", _config.MaxDecodingLength },
        };
    }

    public void SetValue(string key, object value)
    {
        switch (key)
        {
            case "model":
                _config.Model = value?.ToString() ?? "";
                break;
            case "custom_model_path":
                _config.CustomModelPath = value?.ToString() ?? "";
                break;
            case "beam_size":
                _config.BeamSize = Convert.ToInt32(value);
                break;
            case "max_decoding_length":
                _config.MaxDecodingLength = Convert.ToInt32(value);
                break;
        }
        ValueUpdated?.Invoke(this, EventArgs.Empty);
    }

    public object GetValue(string key)
    {
        return key switch
        {
            "model" => _config.Model,
            "custom_model_path" => _config.CustomModelPath,
            "beam_size" => _config.BeamSize,
            "max_decoding_length" => _config.MaxDecodingLength,
            _ => ""
        };
    }

    public string GenerateConfig()
    {
        return JsonSerializer.Serialize(_config);
    }

    public void LoadConfigString(string config)
    {
        if (string.IsNullOrEmpty(config)) return;
        try
        {
            _config = JsonSerializer.Deserialize<LocalEnZhConfig>(config) ?? new LocalEnZhConfig();
        }
        catch
        {
            _config = new LocalEnZhConfig();
        }
    }
}
