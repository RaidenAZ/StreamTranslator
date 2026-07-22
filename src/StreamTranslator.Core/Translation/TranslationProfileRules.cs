using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StreamTranslator.Core.Configuration;

namespace StreamTranslator.Core.Translation;

public static class TranslationProfileRules
{
    private const int MaxExtraBodyBytes = 16 * 1024;
    private static readonly HashSet<string> ForbiddenExtraBodyProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "model", "messages", "stream", "tools", "tool_choice", "functions", "function_call",
        "response_format", "headers", "authorization", "api_key", "apiKey", "base_url", "url"
    };

    public static IReadOnlyList<string> Validate(TranslationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var errors = new List<string>();

        if (profile.Id == Guid.Empty)
        {
            errors.Add("配置 ID 无效。");
        }

        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            errors.Add("配置名称不能为空。");
        }

        if (string.IsNullOrWhiteSpace(profile.Model))
        {
            errors.Add("模型名称不能为空。");
        }

        if (!TryGetBaseUri(profile.BaseUrl, out var uri, out var urlError))
        {
            errors.Add(urlError);
        }
        else
        {
            if (profile.Location == TranslationServiceLocation.Remote && uri.Scheme != Uri.UriSchemeHttps)
            {
                errors.Add("远程翻译服务必须使用 HTTPS。");
            }

            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                errors.Add("Base URL 不能包含 userinfo。");
            }

            if (!string.IsNullOrEmpty(uri.Query))
            {
                errors.Add("Base URL 不能包含 query。");
            }

            if (!string.IsNullOrEmpty(uri.Fragment))
            {
                errors.Add("Base URL 不能包含 fragment。");
            }

            if (uri.AbsolutePath.TrimEnd('/').EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("Base URL 不应包含 chat/completions，应用会解析该固定路径。");
            }
        }

        if (profile.TimeoutMs is < 3000 or > 30000)
        {
            errors.Add("翻译请求超时必须在 3000 到 30000 ms 之间。");
        }

        if (profile.MaxConcurrency is < 1 or > 4)
        {
            errors.Add("翻译最大并发必须在 1 到 4 之间。");
        }

        if (profile.RequestCompatibility == TranslationRequestCompatibility.Custom)
        {
            ValidateCustomExtraBody(profile.CustomExtraBody, errors);
        }

        return errors;
    }

    public static string NormalizeBaseUrl(string baseUrl)
    {
        if (!TryGetBaseUri(baseUrl, out var uri, out var error))
        {
            throw new ArgumentException(error, nameof(baseUrl));
        }

        var builder = new UriBuilder(uri) { Fragment = "", Query = "" };
        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }

    public static string BuildFinalEndpoint(string baseUrl)
    {
        return $"{NormalizeBaseUrl(baseUrl)}/chat/completions";
    }

    public static TranslationServiceLocation SuggestLocation(string baseUrl)
    {
        if (!TryGetBaseUri(baseUrl, out var uri, out _))
        {
            return TranslationServiceLocation.Remote;
        }

        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            IPAddress.TryParse(uri.Host, out var address) && IsLocalAddress(address))
        {
            return TranslationServiceLocation.Local;
        }

        return TranslationServiceLocation.Remote;
    }

    public static JsonElement ResolveExtraBody(TranslationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var json = profile.RequestCompatibility switch
        {
            TranslationRequestCompatibility.Standard => "{}",
            TranslationRequestCompatibility.DeepSeek => "{\"thinking\":{\"type\":\"disabled\"}}",
            TranslationRequestCompatibility.QwenVllm => "{\"chat_template_kwargs\":{\"enable_thinking\":false}}",
            TranslationRequestCompatibility.Custom => profile.CustomExtraBody.GetRawText(),
            _ => throw new ArgumentOutOfRangeException(nameof(profile), "Unknown request compatibility template.")
        };
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    public static string CreateValidationFingerprint(TranslationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var material = string.Join('\n',
            NormalizeBaseUrl(profile.BaseUrl),
            profile.Model,
            profile.ApiKey,
            profile.RequestCompatibility,
            ResolveExtraBody(profile).GetRawText());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    public static bool IsValidated(TranslationProfile profile)
    {
        return !string.IsNullOrWhiteSpace(profile.ValidationFingerprint) &&
               CryptographicOperations.FixedTimeEquals(
                   Encoding.ASCII.GetBytes(profile.ValidationFingerprint),
                   Encoding.ASCII.GetBytes(CreateValidationFingerprint(profile)));
    }

    private static bool TryGetBaseUri(string baseUrl, out Uri uri, out string error)
    {
        if (!Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out uri!) ||
            uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            error = "Base URL 必须是有效的 HTTP 或 HTTPS 地址。";
            return false;
        }

        error = "";
        return true;
    }

    private static bool IsLocalAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10 ||
                   bytes[0] == 192 && bytes[1] == 168 ||
                   bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                   bytes[0] == 169 && bytes[1] == 254;
        }

        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal;
    }

    private static void ValidateCustomExtraBody(JsonElement body, ICollection<string> errors)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            errors.Add("自定义 extraBody 顶层必须是 JSON object。");
            return;
        }

        if (Encoding.UTF8.GetByteCount(body.GetRawText()) > MaxExtraBodyBytes)
        {
            errors.Add("自定义 extraBody 不能超过 16KB。");
        }

        ValidateProperties(body, errors);
    }

    private static void ValidateProperties(JsonElement element, ICollection<string> errors)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (ForbiddenExtraBodyProperties.Contains(property.Name))
                {
                    errors.Add($"自定义 extraBody 不能包含保留字段 {property.Name}。");
                }

                ValidateProperties(property.Value, errors);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                ValidateProperties(item, errors);
            }
        }
    }
}
