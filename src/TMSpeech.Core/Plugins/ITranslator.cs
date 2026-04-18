using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TMSpeech.Core.Plugins
{
    public class TranslationEventArgs : EventArgs
    {
        public string OriginalText { get; set; } = "";
        public string TranslatedText { get; set; } = "";
        /// <summary>
        /// true = 定稿翻译 (from SentenceDone), false = 临时翻译 (from TextChanged)
        /// </summary>
        public bool IsFinal { get; set; }
    }

    public class TranslationContextItem
    {
        public string SourceText { get; set; } = "";
        public string TranslatedText { get; set; } = "";
    }

    public interface ITranslator : IPlugin
    {
        /// <summary>
        /// 翻译完成事件（包括临时翻译和定稿翻译）
        /// </summary>
        event EventHandler<TranslationEventArgs> TranslationCompleted;

        /// <summary>
        /// 异步请求翻译。通过 TranslationCompleted 事件返回结果。
        /// </summary>
        /// <param name="text">待翻译文本</param>
        /// <param name="isFinal">true=定稿翻译, false=临时翻译</param>
        void TranslateAsync(string text, bool isFinal);

        /// <summary>
        /// 设置翻译上下文（前序已完成句子的原文和译文），用于提升翻译连贯性
        /// </summary>
        void SetContext(IReadOnlyList<TranslationContextItem> context);

        /// <summary>
        /// 同步翻译（便捷接口，可选实现）
        /// </summary>
        string Translate(string text);

        /// <summary>
        /// 翻译器运行时异常事件
        /// </summary>
        event EventHandler<Exception>? ExceptionOccured;
    }
}
