using System.Runtime.InteropServices;
using System.Text;

namespace TMSpeech.Translator.LocalEnZh;

/// <summary>
/// CTranslate2 native library wrapper.
/// Provides high-level translate API on top of P/Invoke declarations.
/// </summary>
internal sealed class Ct2Translator : IDisposable
{
    private IntPtr _translator = IntPtr.Zero;
    private bool _disposed;

    /// <summary>
    /// Load a CTranslate2 model from the given directory path.
    /// </summary>
    public void LoadModel(string modelPath)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(Ct2Translator));

        FreeTranslator();

        _translator = Ct2NativeMethods.ctranslate2_translator_new(
            modelPath,
            device: 0,  // CPU
            compute_type: IntPtr.Zero,
            device_indices: IntPtr.Zero,
            num_device_indices: 0,
            options: IntPtr.Zero
        );

        if (_translator == IntPtr.Zero)
            throw new InvalidOperationException($"Failed to create CTranslate2 translator from: {modelPath}");
    }

    /// <summary>
    /// Translate a sequence of source tokens.
    /// </summary>
    /// <param name="sourceTokens">Pre-tokenized source tokens</param>
    /// <param name="beamSize">Beam search width (1 for greedy)</param>
    /// <param name="maxDecodingLength">Maximum output token count</param>
    /// <returns>Translated token sequence</returns>
    public List<string> TranslateTokens(IEnumerable<string> sourceTokens, int beamSize = 4, int maxDecodingLength = 256)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(Ct2Translator));
        if (_translator == IntPtr.Zero)
            throw new InvalidOperationException("Translator model not loaded");

        var sourceList = sourceTokens.ToList();
        if (sourceList.Count == 0)
            return new List<string>();

        // Allocate native string arrays
        var sourceHandles = sourceList.Select(s => Marshal.StringToHGlobalAnsi(s)).ToArray();
        var sourcePtrArray = Marshal.AllocHGlobal(sourceHandles.Length * IntPtr.Size);
        for (int i = 0; i < sourceHandles.Length; i++)
            Marshal.WriteIntPtr(sourcePtrArray, i * IntPtr.Size, sourceHandles[i]);

        // Create translation options
        var options = Ct2NativeMethods.ctranslate2_translation_options_new();
        if (options == IntPtr.Zero)
            throw new InvalidOperationException("Failed to create translation options");

        Ct2NativeMethods.ctranslate2_translation_options_set_beam_size(options, (UIntPtr)beamSize);
        Ct2NativeMethods.ctranslate2_translation_options_set_max_decoding_length(options, (UIntPtr)maxDecodingLength);

        try
        {
            // Execute translation
            var result = Ct2NativeMethods.ctranslate2_translator_translate(
                _translator,
                (UIntPtr)1,               // num_source_batches = 1
                sourcePtrArray,            // source tokens
                UIntPtr.Zero,              // no target prefix
                IntPtr.Zero,               // no target prefix ptr
                options
            );

            if (result == IntPtr.Zero)
                throw new InvalidOperationException("Translation returned null result");

            try
            {
                return ReadTranslationResult(result);
            }
            finally
            {
                Ct2NativeMethods.ctranslate2_translation_result_delete(result);
            }
        }
        finally
        {
            Ct2NativeMethods.ctranslate2_translation_options_delete(options);
            foreach (var h in sourceHandles) Marshal.FreeHGlobal(h);
            Marshal.FreeHGlobal(sourcePtrArray);
        }
    }

    /// <summary>
    /// Translate a sequence of source token arrays (batch with context lines).
    /// Each element in sourceBatches is a separate source line (context + current).
    /// CTranslate2 will use previous lines as prefix context.
    /// </summary>
    public List<string> TranslateBatchTokens(List<List<string>> sourceBatches, int beamSize = 4, int maxDecodingLength = 256)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(Ct2Translator));
        if (_translator == IntPtr.Zero)
            throw new InvalidOperationException("Translator model not loaded");

        // For simplicity, translate only the last batch item (current sentence)
        // with context prepended. CT2 batch API handles this differently.
        // We use the single-translate approach with context concatenated.
        if (sourceBatches.Count == 0)
            return new List<string>();

        // Concatenate context tokens and current sentence tokens with special separators
        var allTokens = new List<string>();
        for (int i = 0; i < sourceBatches.Count - 1; i++)
        {
            allTokens.AddRange(sourceBatches[i]);
            allTokens.Add("</s>");  // sentence separator
        }
        allTokens.AddRange(sourceBatches[^1]);

        return TranslateTokens(allTokens, beamSize, maxDecodingLength);
    }

    private static List<string> ReadTranslationResult(IntPtr result)
    {
        var translation = new List<string>();
        var numHypotheses = Ct2NativeMethods.ctranslate2_translation_result_get_num_hypotheses(result);

        if (numHypotheses == UIntPtr.Zero)
            return translation;

        // Read the first hypothesis
        var hypothesisIdx = UIntPtr.Zero;
        var numTokens = Ct2NativeMethods.ctranslate2_translation_result_get_num_translations(result, hypothesisIdx);

        for (ulong i = 0; i < numTokens.ToUInt64(); i++)
        {
            var tokenPtr = Ct2NativeMethods.ctranslate2_translation_result_get_translation(result, hypothesisIdx, new UIntPtr(i));
            if (tokenPtr != IntPtr.Zero)
            {
                var token = Marshal.PtrToStringAnsi(tokenPtr) ?? "";
                // Skip end-of-sentence token
                if (token != "</s>")
                    translation.Add(token);
            }
        }

        return translation;
    }

    private void FreeTranslator()
    {
        if (_translator != IntPtr.Zero)
        {
            Ct2NativeMethods.ctranslate2_translator_delete(_translator);
            _translator = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        FreeTranslator();
        _disposed = true;
    }
}
