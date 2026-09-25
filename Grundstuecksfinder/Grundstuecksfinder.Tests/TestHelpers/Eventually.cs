using FluentAssertions;
using Xunit;

namespace Grundstuecksfinder.Tests.TestHelpers;

/// <summary>For background services: polls until they got somewhere, or fails after ten seconds.</summary>
public static class Eventually
{
    /// <param name="condition">What the service should have reached.</param>
    /// <param name="nudge">
    /// Called before every check, e.g. to advance a fake clock: a service may not have started
    /// its delay yet when the test first advances it.
    /// </param>
    public static async Task WaitUntilAsync(Func<bool> condition, Action? nudge = null)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            nudge?.Invoke();
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        condition().Should().BeTrue("the service should have got there within 10 seconds");
    }
}
