using System.Text;

namespace TMSpeech.Translator.LocalEnZh;

/// <summary>
/// Simple tokenizer for OPUS-MT models.
/// Uses basic whitespace + punctuation splitting as fallback when SentencePiece is not available.
/// For production use, integrate with SentencePiece.NET for accurate tokenization.
/// </summary>
internal class SimpleTokenizer
{
    /// <summary>
    /// Tokenize English text for OPUS-MT model input.
    /// Applies basic Moses-style tokenization: split on punctuation, preserve spacing.
    /// </summary>
    public static List<string> Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<string>();

        // Basic Moses-style tokenization
        var tokens = new List<string>();
        var current = new StringBuilder();

        foreach (var ch in text.Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else if (IsPunctuation(ch))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                tokens.Add(ch.ToString());
            }
            else
            {
                current.Append(ch);
            }
        }

        if (current.Length > 0)
            tokens.Add(current.ToString());

        // Add sentence start/end markers expected by OPUS-MT
        var result = new List<string> { "▁" };  // sentence piece start marker
        foreach (var token in tokens)
        {
            // Simple subword: prefix with ▁ for word-initial tokens
            result.Add("▁" + token);
        }

        return result;
    }

    /// <summary>
    /// Detokenize OPUS-MT output tokens back to a string.
    /// </summary>
    public static string Detokenize(IEnumerable<string> tokens)
    {
        var sb = new StringBuilder();
        foreach (var token in tokens)
        {
            // Remove SentencePiece markers
            var cleaned = token.Replace("▁", " ");
            sb.Append(cleaned);
        }

        var result = sb.ToString().Trim();

        // Fix spacing around punctuation
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\s+([.,!?;:，。！？；：])", "$1");
        result = System.Text.RegularExpressions.Regex.Replace(result, @"([(\[（【])\s+", "$1");
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\s+([)\]）】])", "$1");

        return result;
    }

    private static bool IsPunctuation(char ch)
    {
        return ch is ',' or '.' or '!' or '?' or ';' or ':' or '(' or ')' or '[' or ']'
            or '{' or '}' or '"' or '\'' or '-' or '/' or '\\'
            or '，' or '。' or '！' or '？' or '；' or '：' or '（' or '）' or '【' or '】';
    }
}
