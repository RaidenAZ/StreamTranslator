namespace StreamTranslator.Core.Translation;

public static class SourceLanguageDecision
{
    public static bool ShouldSkip(string sourceLanguage, string targetLanguage, string text)
    {
        if (string.Equals(sourceLanguage, "zh", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(targetLanguage, "zh-Hans", StringComparison.OrdinalIgnoreCase);
        }

        if (string.Equals(sourceLanguage, "en", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(targetLanguage, "en", StringComparison.OrdinalIgnoreCase);
        }

        if (!string.Equals(sourceLanguage, "auto", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var cjk = 0;
        var latin = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (IsCjk(rune.Value))
            {
                cjk++;
            }
            else if (rune.Value is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
            {
                latin++;
            }
        }

        var letters = cjk + latin;
        if (letters == 0)
        {
            return false;
        }

        if (string.Equals(targetLanguage, "zh-Hans", StringComparison.OrdinalIgnoreCase))
        {
            return cjk >= 4 && latin == 0;
        }

        return string.Equals(targetLanguage, "en", StringComparison.OrdinalIgnoreCase) &&
               latin >= 6 && cjk == 0;
    }

    private static bool IsCjk(int value)
    {
        return value is >= 0x3400 and <= 0x4DBF or
            >= 0x4E00 and <= 0x9FFF or
            >= 0xF900 and <= 0xFAFF;
    }
}
