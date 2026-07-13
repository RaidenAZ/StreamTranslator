namespace StreamTranslator.App.Runtime;

public sealed class RuntimeFatalException(string message, Exception? innerException = null)
    : Exception(message, innerException);
