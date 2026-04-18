using System.Runtime.InteropServices;

namespace TMSpeech.Translator.LocalEnZh;

/// <summary>
/// CTranslate2 C API P/Invoke declarations.
/// Reference: https://github.com/OpenNMT/CTranslate2/blob/master/include/ctranslate2/translator.h
/// </summary>
internal static class Ct2NativeMethods
{
    private const string DllName = "ctranslate2";

    // --- Status / Error ---
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ctranslate2_status_code(IntPtr status);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ctranslate2_status_delete(IntPtr status);

    // --- Translator ---
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr ctranslate2_translator_new(
        [MarshalAs(UnmanagedType.LPStr)] string model_path,
        int device,
        IntPtr compute_type,
        IntPtr device_indices,
        uint num_device_indices,
        IntPtr options
    );

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ctranslate2_translator_delete(IntPtr translator);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr ctranslate2_translator_translate(
        IntPtr translator,
        UIntPtr num_source,
        IntPtr source,           // const char* const*
        UIntPtr num_target_prefix,
        IntPtr target_prefix,    // const char* const*
        IntPtr options
    );

    // --- Translation Options ---
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr ctranslate2_translation_options_new();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ctranslate2_translation_options_delete(IntPtr options);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ctranslate2_translation_options_set_beam_size(
        IntPtr options, UIntPtr beam_size);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ctranslate2_translation_options_set_max_decoding_length(
        IntPtr options, UIntPtr length);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ctranslate2_translation_options_set_patience(
        IntPtr options, float patience);

    // --- Translation Result ---
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern UIntPtr ctranslate2_translation_result_get_num_translations(
        IntPtr result, UIntPtr hypothesis_index);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr ctranslate2_translation_result_get_translation(
        IntPtr result, UIntPtr hypothesis_index, UIntPtr token_index);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern UIntPtr ctranslate2_translation_result_get_num_hypotheses(
        IntPtr result);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern float ctranslate2_translation_result_get_score(
        IntPtr result, UIntPtr hypothesis_index);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ctranslate2_translation_result_delete(IntPtr result);
}
