using Amberkeep.App.Services;

namespace Amberkeep.Tests.App;

public sealed class FormatTests
{
    [Theory]
    [InlineData(0, "0.0 B/s")]
    [InlineData(512, "512.0 B/s")]
    [InlineData(1024 * 84, "84.0 KB/s")]
    [InlineData(1024 * 1024 * 84.25, "84.3 MB/s")]
    [InlineData(1024 * 1024 * 999.0, "999.0 MB/s")]
    [InlineData(1024 * 1024 * 1010.0, "1.0 GB/s")]
    [InlineData(-5, "0.0 B/s")]
    public void Speed_always_has_one_decimal_and_at_most_three_integer_digits(double bytesPerSecond, string expected) =>
        Assert.Equal(expected, Format.Speed(bytesPerSecond));
}
