// VoiceAgent.Tests/Models/CharacterConfigTests.cs
using FluentAssertions;
using VoiceAgent.Models;

namespace VoiceAgent.Tests.Models;

public class CharacterConfigTests
{
    [Theory]
    [InlineData(null, "Invisigal")]
    [InlineData("", "Invisigal")]
    [InlineData("invisigal", "Invisigal")]
    [InlineData("Invisigal", "Invisigal")]
    [InlineData("courtney", "Invisigal")]
    [InlineData("unknown", "Invisigal")]
    public void GetByName_ReturnsInvisigalAsDefault(string? name, string expectedName)
    {
        var character = Characters.GetByName(name);
        character.Name.Should().Be(expectedName);
    }

    [Theory]
    [InlineData("blonde-blazer", "Blonde Blazer")]
    [InlineData("blondeblazer", "Blonde Blazer")]
    [InlineData("blazer", "Blonde Blazer")]
    [InlineData("mandy", "Blonde Blazer")]
    [InlineData("Blonde-Blazer", "Blonde Blazer")]
    public void GetByName_ReturnsBlondeBlazer(string name, string expectedName)
    {
        var character = Characters.GetByName(name);
        character.Name.Should().Be(expectedName);
    }

    [Fact]
    public void Invisigal_HasRequiredProperties()
    {
        var character = Characters.Invisigal;

        character.Name.Should().Be("Invisigal");
        character.Personality.Should().Contain("snarky");
        character.SpeechStyle.Should().Contain("sarcastic");
    }

    [Fact]
    public void BlondeBlazer_HasRequiredProperties()
    {
        var character = Characters.BlondeBlazer;

        character.Name.Should().Be("Blonde Blazer");
        character.Personality.Should().Contain("courageous");
        character.SpeechStyle.Should().Contain("encouraging");
    }

    [Fact]
    public void AvailableCharacters_ListsBothCharacters()
    {
        Characters.AvailableCharacters.Should().Contain("invisigal");
        Characters.AvailableCharacters.Should().Contain("blonde-blazer");
    }
}
