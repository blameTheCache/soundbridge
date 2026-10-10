using SoundBridge.Core.Connection;

namespace SoundBridge.Core.Tests;

public sealed class ReconnectPolicyTests
{
    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(4, 16)]
    [InlineData(5, 32)]
    [InlineData(6, 60)]
    [InlineData(10, 60)]
    [InlineData(500, 60)]
    public void Delay_doubles_from_the_base_and_is_capped(int retry, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), ReconnectPolicy.Default.DelayBefore(retry));
    }

    [Fact]
    public void Delay_rejects_a_retry_number_below_one()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReconnectPolicy.Default.DelayBefore(0));
    }

    [Theory]
    [InlineData(-3, 1)]
    [InlineData(0, 1)]
    [InlineData(5, 5)]
    [InlineData(99, 10)]
    public void Retry_count_is_clamped_to_the_supported_range(int requested, int expected)
    {
        Assert.Equal(expected, ReconnectPolicy.ClampRetries(requested));
    }
}
