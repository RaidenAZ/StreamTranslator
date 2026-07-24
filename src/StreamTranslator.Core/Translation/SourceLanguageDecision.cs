using System.Text.RegularExpressions;

namespace StreamTranslator.Core.Translation;

public static class SourceLanguageDecision
{
    private static readonly HashSet<string> EnglishMarkers = new(StringComparer.Ordinal)
    {
        "a", "about", "and", "are", "as", "at", "be", "can", "for", "from", "has", "have",
        "in", "is", "it", "live", "my", "not", "of", "on", "or", "our", "stream", "that",
        "the", "this", "to", "today", "very", "was", "we", "were", "welcome", "will", "with",
        "you", "your"
    };

    private static readonly HashSet<string> GermanMarkers = new(StringComparer.Ordinal)
    {
        "auf", "der", "die", "das", "dem", "den", "des", "ein", "eine", "einer", "fahrer", "für",
        "heute", "heutigen", "ist", "mit", "nicht", "rennen", "schnell", "sehr", "sind", "und", "von",
        "willkommen", "zum", "zur"
    };

    private static readonly HashSet<string> FrenchMarkers = new(StringComparer.Ordinal)
    {
        "aujourd'hui", "aujourd-hui", "avec", "bienvenue", "dans", "de", "des", "direct", "diffusion",
        "du", "en", "est", "et", "la", "le", "les", "pilote", "pour", "rapide", "sur", "tres", "très",
        "un", "une"
    };

    // These are common simplified-Chinese characters or words that provide stronger evidence than Han script alone.
    private static readonly HashSet<int> ChineseMarkers =
    [
        0x4E0D, 0x4E2A, 0x4E3A, 0x4E48, 0x4EEC, 0x4ECA, 0x4F1A, 0x4F7F, 0x4F60, 0x5148,
        0x5168, 0x5173, 0x5176, 0x5185, 0x51FA, 0x52A8, 0x52A8, 0x53D1, 0x53D8, 0x53EF, 0x5408, 0x540E,
        0x548C, 0x56DE, 0x56E0, 0x56FD, 0x5728, 0x5916, 0x5927, 0x5929, 0x597D, 0x5B50, 0x5B66, 0x5B9E,
        0x5B9A, 0x5C06, 0x5C0F, 0x5C1A, 0x5DE5, 0x5E94, 0x5E74, 0x5F00, 0x5F53, 0x5F88, 0x5FC5, 0x6211,
        0x6240, 0x624D, 0x6295, 0x6307, 0x63A5, 0x63D0, 0x6536, 0x65B0, 0x65F6, 0x662F, 0x66F4, 0x6709,
        0x6765, 0x6B22, 0x6B63, 0x6CA1, 0x6D3B, 0x6D3E, 0x6EE1, 0x6F14, 0x7136, 0x7248, 0x7279, 0x73B0,
        0x7528, 0x7535, 0x7684, 0x767D, 0x76F4, 0x77E5, 0x79CD, 0x7EC4, 0x7ED3, 0x7F51, 0x8001, 0x80FD,
        0x81EA, 0x81F3, 0x8868, 0x89C1, 0x89E3, 0x8BF4, 0x8C03, 0x8D77, 0x8FD8, 0x8FD9, 0x8FDB, 0x8FC7,
        0x8FD9, 0x9009, 0x90A3, 0x91CD, 0x91CC, 0x957F, 0x95EE, 0x9700, 0x8FCE
    ];

    public static bool ShouldSkip(string sourceLanguage, string targetLanguage, string text)
    {
        return Analyze(sourceLanguage, targetLanguage, text).ShouldSkip;
    }

    public static SourceLanguageDecisionResult Analyze(
        string sourceLanguage,
        string targetLanguage,
        string text)
    {
        if (string.Equals(sourceLanguage, "zh", StringComparison.OrdinalIgnoreCase))
        {
            return SameLanguage(targetLanguage, "zh", "explicit source language");
        }

        if (string.Equals(sourceLanguage, "en", StringComparison.OrdinalIgnoreCase))
        {
            return SameLanguage(targetLanguage, "en", "explicit source language");
        }

        if (!string.Equals(sourceLanguage, "auto", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(text))
        {
            return Unknown("source language is not auto or text is empty");
        }

        var profile = TextProfile.Create(text);
        if (profile.KanaCount >= 2 && profile.CjkCount >= 1 && profile.LatinCount <= 4)
        {
            return SameLanguage(targetLanguage, "ja", "Japanese kana evidence", 95);
        }

        if (profile.KanaCount >= 2 && profile.CjkCount == 0 && profile.LatinCount == 0)
        {
            return SameLanguage(targetLanguage, "ja", "Japanese kana evidence", 95);
        }

        if (profile.LatinCount == 0 && profile.KanaCount == 0 && profile.CjkCount >= 4 &&
            profile.ChineseMarkerCount >= 3)
        {
            return SameLanguage(targetLanguage, "zh", "simplified Chinese evidence", 95);
        }

        var scores = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["en"] = Score(profile.Words, EnglishMarkers),
            ["de"] = Score(profile.Words, GermanMarkers),
            ["fr"] = Score(profile.Words, FrenchMarkers)
        };
        var best = scores.MaxBy(pair => pair.Value);
        var second = scores
            .Where(pair => !string.Equals(pair.Key, best.Key, StringComparison.Ordinal))
            .Max(pair => pair.Value);

        if (best.Value >= 2 && best.Value >= second + 2)
        {
            return SameLanguage(targetLanguage, best.Key, $"{best.Key} lexical evidence", 85);
        }

        return Unknown("language evidence is ambiguous");
    }

    private static SourceLanguageDecisionResult SameLanguage(
        string targetLanguage,
        string detectedLanguage,
        string reason,
        int confidence = 100)
    {
        var same = Normalize(targetLanguage) == detectedLanguage;
        return new SourceLanguageDecisionResult(
            same,
            detectedLanguage,
            confidence,
            same ? reason : $"detected {detectedLanguage}, target {Normalize(targetLanguage)}");
    }

    private static SourceLanguageDecisionResult Unknown(string reason)
    {
        return new SourceLanguageDecisionResult(false, null, 0, reason);
    }

    private static string Normalize(string language)
    {
        return language.Trim().ToLowerInvariant() switch
        {
            "zh" or "zh-hans" or "zh-cn" => "zh",
            "en" or "en-us" or "en-gb" => "en",
            "de" or "de-de" => "de",
            "fr" or "fr-fr" => "fr",
            "ja" or "ja-jp" => "ja",
            _ => language.Trim().ToLowerInvariant()
        };
    }

    private static int Score(IEnumerable<string> words, HashSet<string> markers)
    {
        return words.Count(markers.Contains);
    }

    private static bool IsCjk(int value)
    {
        return value is >= 0x3400 and <= 0x4DBF or
            >= 0x4E00 and <= 0x9FFF or
            >= 0xF900 and <= 0xFAFF;
    }

    private static bool IsKana(int value)
    {
        return value is >= 0x3040 and <= 0x30FF or
            >= 0x31A0 and <= 0x31BF or
            >= 0xFF66 and <= 0xFF9F;
    }

    private static bool IsLatin(int value)
    {
        return value is >= 'A' and <= 'Z' or
            >= 'a' and <= 'z' or
            >= 0x00C0 and <= 0x02AF;
    }

    private sealed record TextProfile(
        int CjkCount,
        int KanaCount,
        int LatinCount,
        int ChineseMarkerCount,
        IReadOnlyList<string> Words)
    {
        public static TextProfile Create(string text)
        {
            var cjk = 0;
            var kana = 0;
            var latin = 0;
            var chineseMarkers = 0;
            foreach (var rune in text.EnumerateRunes())
            {
                if (IsCjk(rune.Value))
                {
                    cjk++;
                    if (ChineseMarkers.Contains(rune.Value))
                    {
                        chineseMarkers++;
                    }
                }
                else if (IsKana(rune.Value))
                {
                    kana++;
                }
                else if (IsLatin(rune.Value))
                {
                    latin++;
                }
            }

            var words = Regex.Split(text.ToLowerInvariant(), @"[^\p{L}']+")
                .Where(static word => !string.IsNullOrWhiteSpace(word))
                .Select(static word => word.Replace("'", string.Empty, StringComparison.Ordinal))
                .ToArray();
            return new TextProfile(cjk, kana, latin, chineseMarkers, words);
        }
    }
}

public readonly record struct SourceLanguageDecisionResult(
    bool ShouldSkip,
    string? DetectedLanguage,
    int Confidence,
    string Reason);
