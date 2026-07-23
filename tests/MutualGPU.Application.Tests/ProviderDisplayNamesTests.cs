using MutualGPU.Application;

namespace MutualGPU.Application.Tests;

public sealed class ProviderDisplayNamesTests
{
    [Fact]
    public void Display_name_is_stable_readable_and_ends_with_the_literal_passcode_prefix()
    {
        const string passcode = "ABCD-this-is-a-long-random-provider-passcode";

        var first = ProviderDisplayNames.Create(passcode);
        var second = ProviderDisplayNames.Create(passcode);

        Assert.Equal(first, second);
        Assert.Matches("^[A-Z][a-z]+ [A-Z][a-z]+ · ABCD$", first);
        Assert.DoesNotContain(passcode, first, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_name_requires_a_passcode() =>
        Assert.Throws<ArgumentException>(() => ProviderDisplayNames.Create(" "));
}
