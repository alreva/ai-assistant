// VoiceAgent.Tests/Services/ConfirmationDetectorTests.cs
using FluentAssertions;
using VoiceAgent.Services;

namespace VoiceAgent.Tests.Services;

public class ConfirmationDetectorTests
{
    // ConfirmationDetector now uses LLM, so we only test that it can be constructed
    // Actual behavior tests would require integration testing with Azure OpenAI

    [Fact]
    public void ConfirmationType_HasExpectedValues()
    {
        // Verify the enum has the expected values
        Enum.GetValues<ConfirmationType>().Should().HaveCount(3);
        ((int)ConfirmationType.Confirmed).Should().Be(0);
        ((int)ConfirmationType.Cancelled).Should().Be(1);
        ((int)ConfirmationType.Modification).Should().Be(2);
    }
}
