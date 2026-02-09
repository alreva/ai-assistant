// VoiceAgent.Tests/Services/IntentClassifierTests.cs
using FluentAssertions;
using VoiceAgent.Services;

namespace VoiceAgent.Tests.Services;

public class IntentClassifierTests
{
    [Theory]
    [InlineData("show my entries", IntentType.Query)]
    [InlineData("list all projects", IntentType.Query)]
    [InlineData("what time did I log", IntentType.Query)]
    [InlineData("how many hours", IntentType.Query)]
    public void ClassifyIntent_QueryPhrases_ReturnsQuery(string text, IntentType expected)
    {
        var classifier = new IntentClassifier();

        var result = classifier.ClassifyIntent(text);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("log 8 hours", IntentType.Update)]
    [InlineData("delete the entry", IntentType.Update)]
    [InlineData("move yesterday's time", IntentType.Update)]
    [InlineData("submit for approval", IntentType.Update)]
    [InlineData("update the hours", IntentType.Update)]
    public void ClassifyIntent_UpdatePhrases_ReturnsUpdate(string text, IntentType expected)
    {
        var classifier = new IntentClassifier();

        var result = classifier.ClassifyIntent(text);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("yes", IntentType.Confirmation)]
    [InlineData("confirm", IntentType.Confirmation)]
    [InlineData("do it", IntentType.Confirmation)]
    [InlineData("go ahead", IntentType.Confirmation)]
    public void ClassifyIntent_ConfirmationPhrases_ReturnsConfirmation(string text, IntentType expected)
    {
        var classifier = new IntentClassifier();

        var result = classifier.ClassifyIntent(text);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("no", IntentType.Cancellation)]
    [InlineData("cancel", IntentType.Cancellation)]
    [InlineData("never mind", IntentType.Cancellation)]
    [InlineData("stop", IntentType.Cancellation)]
    public void ClassifyIntent_CancellationPhrases_ReturnsCancellation(string text, IntentType expected)
    {
        var classifier = new IntentClassifier();

        var result = classifier.ClassifyIntent(text);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("goodbye", IntentType.EndSession)]
    [InlineData("done", IntentType.EndSession)]
    [InlineData("that's all", IntentType.EndSession)]
    public void ClassifyIntent_EndSessionPhrases_ReturnsEndSession(string text, IntentType expected)
    {
        var classifier = new IntentClassifier();

        var result = classifier.ClassifyIntent(text);

        result.Should().Be(expected);
    }
}
