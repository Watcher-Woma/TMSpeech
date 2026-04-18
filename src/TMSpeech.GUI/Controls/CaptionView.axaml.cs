using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace TMSpeech.GUI.Views;

public partial class CaptionView : UserControl
{
    public CaptionView()
    {
        InitializeComponent();
    }

    public static readonly StyledProperty<Color> ShadowColorProperty = AvaloniaProperty.Register<CaptionView, Color>(
        "ShadowColor", Colors.Black);

    public Color ShadowColor
    {
        get => GetValue(ShadowColorProperty);
        set => SetValue(ShadowColorProperty, value);
    }

    public static readonly StyledProperty<int> ShadowSizeProperty = AvaloniaProperty.Register<CaptionView, int>(
        "ShadowSize", 10);

    public int ShadowSize
    {
        get => GetValue(ShadowSizeProperty);
        set => SetValue(ShadowSizeProperty, value);
    }

    public static readonly StyledProperty<Color> FontColorProperty = AvaloniaProperty.Register<CaptionView, Color>(
        "FontColor", Colors.White);

    public Color FontColor
    {
        get => GetValue(FontColorProperty);
        set => SetValue(FontColorProperty, value);
    }

    public static readonly StyledProperty<TextAlignment> TextAlignProperty =
        AvaloniaProperty.Register<CaptionView, TextAlignment>(
            "TextAlign", TextAlignment.Left);

    public TextAlignment TextAlign
    {
        get => GetValue(TextAlignProperty);
        set => SetValue(TextAlignProperty, value);
    }


    public static readonly StyledProperty<string> TextProperty = AvaloniaProperty.Register<CaptionView, string>(
        "Text");

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    // Translation support

    public static readonly StyledProperty<string> TranslatedTextProperty = AvaloniaProperty.Register<CaptionView, string>(
        "TranslatedText");

    public string TranslatedText
    {
        get => GetValue(TranslatedTextProperty);
        set => SetValue(TranslatedTextProperty, value);
    }

    public static readonly StyledProperty<Color> TranslatedFontColorProperty = AvaloniaProperty.Register<CaptionView, Color>(
        "TranslatedFontColor", Colors.LightGray);

    public Color TranslatedFontColor
    {
        get => GetValue(TranslatedFontColorProperty);
        set => SetValue(TranslatedFontColorProperty, value);
    }

    public static readonly StyledProperty<int> TranslatedFontSizeProperty = AvaloniaProperty.Register<CaptionView, int>(
        "TranslatedFontSize", 32);

    public int TranslatedFontSize
    {
        get => GetValue(TranslatedFontSizeProperty);
        set => SetValue(TranslatedFontSizeProperty, value);
    }

    public static readonly StyledProperty<bool> HasTranslationProperty = AvaloniaProperty.Register<CaptionView, bool>(
        "HasTranslation", false);

    public bool HasTranslation
    {
        get => GetValue(HasTranslationProperty);
        set => SetValue(HasTranslationProperty, value);
    }
}