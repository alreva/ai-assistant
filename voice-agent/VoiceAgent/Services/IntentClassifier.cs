// VoiceAgent/Services/IntentClassifier.cs
namespace VoiceAgent.Services;

public enum IntentType
{
    Query,
    Update,
    Confirmation,
    Cancellation,
    EndSession,
    Unknown
}

public class IntentClassifier
{
    private static readonly string[] QueryKeywords = ["show", "list", "what", "how many", "get", "display"];
    private static readonly string[] UpdateKeywords = ["log", "delete", "move", "submit", "update", "add", "create", "remove"];
    private static readonly string[] ConfirmationKeywords = ["yes", "confirm", "do it", "go ahead", "proceed", "okay", "ok"];
    private static readonly string[] CancellationKeywords = ["no", "cancel", "never mind", "stop", "abort", "don't"];
    private static readonly string[] EndSessionKeywords = ["goodbye", "bye", "done", "that's all", "exit", "quit"];

    public IntentType ClassifyIntent(string text)
    {
        var lowerText = text.ToLowerInvariant();

        // Check end session first (short phrases)
        if (EndSessionKeywords.Any(k => lowerText.Contains(k)))
            return IntentType.EndSession;

        // Check query vs update first (longer phrases take precedence)
        // Query keywords typically start the sentence
        if (QueryKeywords.Any(k => lowerText.Contains(k)))
            return IntentType.Query;

        if (UpdateKeywords.Any(k => lowerText.Contains(k)))
            return IntentType.Update;

        // Check confirmation/cancellation last (usually short standalone responses)
        // Use word boundary matching to avoid false positives like "yesterday" matching "yes"
        if (ConfirmationKeywords.Any(k => ContainsWholeWord(lowerText, k)))
            return IntentType.Confirmation;

        if (CancellationKeywords.Any(k => ContainsWholeWord(lowerText, k)))
            return IntentType.Cancellation;

        return IntentType.Unknown;
    }

    private static bool ContainsWholeWord(string text, string word)
    {
        // For multi-word phrases, just check contains
        if (word.Contains(' '))
            return text.Contains(word);

        // For single words, check word boundaries
        var index = text.IndexOf(word, StringComparison.Ordinal);
        while (index >= 0)
        {
            var beforeOk = index == 0 || !char.IsLetter(text[index - 1]);
            var afterOk = index + word.Length >= text.Length || !char.IsLetter(text[index + word.Length]);

            if (beforeOk && afterOk)
                return true;

            index = text.IndexOf(word, index + 1, StringComparison.Ordinal);
        }
        return false;
    }
}
