using TermCity.Core.Simulation;

namespace TermCity.Tests;

public class CompactMoneyTests
{
    [Theory]
    [InlineData(0, "0g")]
    [InlineData(999_999, "999,999g")]
    [InlineData(1_000_000, "1,000,000g")]
    [InlineData(1_000_001, "1.00M")]
    [InlineData(1_010_000, "1.01M")]
    [InlineData(12_345_678, "12.35M")]
    [InlineData(-1_010_000, "-1.01M")]
    [InlineData(int.MinValue, "-2147.48M")]
    public void HeaderGoldAbbreviatesAmountsOverOneMillion(int amount, string expected) =>
        Assert.Equal(expected, Fmt.CompactMoney(amount));
}
