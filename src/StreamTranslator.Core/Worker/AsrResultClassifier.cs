using StreamTranslator.Core.Subtitles;

namespace StreamTranslator.Core.Worker;

public static class AsrResultClassifier
{
    public static SubtitleStatus Classify(WorkerResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (!response.Ok)
        {
            return SubtitleStatus.Failed;
        }

        // A successful call without text means the segment held no transcribable
        // speech (music, breaths, effects the VAD let through). DeepSeek ASR answers
        // such segments with an empty "text"; that is a valid result, not a failure.
        return string.IsNullOrWhiteSpace(response.Text)
            ? SubtitleStatus.NoSpeech
            : SubtitleStatus.Final;
    }
}
