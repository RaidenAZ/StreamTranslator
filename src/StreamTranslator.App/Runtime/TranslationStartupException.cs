namespace StreamTranslator.App.Runtime;

public sealed class TranslationStartupException(string message, Exception? innerException = null)
    : Exception(message, innerException);
