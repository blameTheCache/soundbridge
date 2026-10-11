using SoundBridge.Core.Audio;

namespace SoundBridge.Core.Tests;

public sealed class PhoneAudioSessionsTests
{
    [Theory]
    [InlineData("Microphone (Pixel A2DP SNK)", "Pixel")]
    [InlineData("Microphone (Sam's Galaxy S24 A2DP SNK)", "Sam's Galaxy S24")]
    [InlineData("Mikrofon (PIXEL a2dp snk)", " Pixel ")]
    public void Recognises_the_music_session_of_the_phone(string session, string phone)
    {
        Assert.True(PhoneAudioSessions.IsMusicSessionOf(session, phone));
    }

    [Theory]
    [InlineData("Microphone (Pixel Hands-Free HF Audio)", "Pixel")]
    [InlineData("Microphone (Galaxy A2DP SNK)", "Pixel")]
    [InlineData("Spotify", "Pixel")]
    [InlineData("", "Pixel")]
    [InlineData(null, "Pixel")]
    public void Rejects_anything_else(string? session, string phone)
    {
        Assert.False(PhoneAudioSessions.IsMusicSessionOf(session, phone));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_phone_without_a_name_matches_nothing(string? phone)
    {
        // An empty name is contained in every string; it must not turn into "any phone's music".
        Assert.False(PhoneAudioSessions.IsMusicSessionOf("Microphone (Pixel A2DP SNK)", phone));
    }
}
