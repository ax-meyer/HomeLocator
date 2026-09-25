using FluentAssertions;
using Grundstuecksfinder.Data;
using Grundstuecksfinder.Models;
using Grundstuecksfinder.Services;
using Grundstuecksfinder.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Grundstuecksfinder.Tests;

public sealed class SearchThrottleTests : IAsyncLifetime
{
    private readonly FakeTimeProvider _time = new();
    private AppDbContext _context = null!;
    private PropertyService _properties = null!;
    private readonly CapturingLogger<SearchGate> _gateLog = new();
    private readonly CapturingLogger<SearchThrottle> _throttleLog = new();

    public async ValueTask InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new AppDbContext(options);
        _context.Properties.Add(new Property { Str = "Hauptstraße", Hnr = "1", Plz = "50667", Ort = "Köln", Gemeinde = "Köln" });
        await _context.SaveChangesAsync();
        _properties = new PropertyService(_context, DisabledSources.None);
    }

    public async ValueTask DisposeAsync() => await _context.DisposeAsync();

    private SearchThrottle Throttle(SearchLimits limits, SearchGate? gate = null) =>
        new(_properties, gate ?? new SearchGate(limits, _gateLog), limits, _time, _throttleLog);

    private static Task<List<Property>> Search(SearchThrottle throttle) =>
        throttle.SearchAsync(null, "50667", null, null, 500);

    [Fact]
    public async Task SearchAsync_WithinBudget_ReturnsResults()
    {
        var result = await Search(Throttle(new SearchLimits()));

        result.Should().ContainSingle();
    }

    [Fact]
    public async Task SearchAsync_PastThePerCircuitBudget_IsRejectedUntilTheWindowPasses()
    {
        var throttle = Throttle(new SearchLimits { PerCircuit = 2, Window = TimeSpan.FromMinutes(1) });

        await Search(throttle);
        await Search(throttle);
        var tooMany = () => Search(throttle);
        await tooMany.Should().ThrowAsync<SearchRejectedException>();
        _throttleLog.MessagesAt(LogLevel.Warning).Should().ContainSingle().Which.Should().Contain("budget of 2 searches");

        _time.Advance(TimeSpan.FromMinutes(1));
        (await Search(throttle)).Should().ContainSingle();
    }

    [Fact]
    public async Task SearchAsync_BudgetIsPerCircuit()
    {
        var limits = new SearchLimits { PerCircuit = 1 };
        var gate = new SearchGate(limits, _gateLog);

        await Search(Throttle(limits, gate));

        (await Search(Throttle(limits, gate))).Should().ContainSingle();
    }

    [Fact]
    public async Task SearchAsync_QueryPastItsTimeout_IsReportedAsRejected()
    {
        var throttle = Throttle(new SearchLimits { QueryTimeout = TimeSpan.Zero });

        var slow = () => Search(throttle);

        (await slow.Should().ThrowAsync<SearchRejectedException>())
            .WithMessage("*zu lange*");
        _throttleLog.MessagesAt(LogLevel.Warning).Should().ContainSingle().Which.Should().Contain("ran longer");
    }

    [Fact]
    public async Task Gate_WhenAllSlotsAreTaken_TurnsTheNextSearchAway_AndFreesTheSlotOnDispose()
    {
        var gate = new SearchGate(new SearchLimits { Concurrent = 1, QueueWait = TimeSpan.FromMilliseconds(50) }, _gateLog);

        var first = await gate.EnterAsync(CancellationToken.None);
        var second = () => gate.EnterAsync(CancellationToken.None);
        await second.Should().ThrowAsync<SearchRejectedException>();
        _gateLog.MessagesAt(LogLevel.Warning).Should().ContainSingle().Which.Should().Contain("slots stayed busy");

        first.Dispose();
        first.Dispose(); // a repeated dispose must not hand out a second slot
        using var third = await gate.EnterAsync(CancellationToken.None);
        var fourth = () => gate.EnterAsync(CancellationToken.None);
        await fourth.Should().ThrowAsync<SearchRejectedException>();
    }
}
