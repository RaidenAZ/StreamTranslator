using StreamTranslator.Core.Configuration;

namespace StreamTranslator.Core.Translation;

public interface ITranslationWorkerClient : IAsyncDisposable
{
    Task<TranslationWorkerResponse> StartAsync(
        TranslationProfile profile,
        CancellationToken cancellationToken = default);

    Task<TranslationWorkerResponse> TranslateAsync(
        TranslationWorkerRequest request,
        CancellationToken cancellationToken = default);

    Task ShutdownAsync(CancellationToken cancellationToken = default);
}
