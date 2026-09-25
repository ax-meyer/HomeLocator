using Xunit;
using Grundstuecksfinder.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Grundstuecksfinder.Tests.TestHelpers;

/// <summary>
/// Shared Testcontainers fixture: one PostgreSQL container per test collection.
/// Schema is created once; individual tests are responsible for clearing state.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    // No explicit wait strategy: the PostgreSql module's default waits until the
    // server actually accepts connections. The previous bare TCP port probe was
    // weaker, since the port opens before Postgres is ready to serve queries.
    private readonly PostgreSqlContainer _container =
        new PostgreSqlBuilder("postgres:17-alpine").Build();

    public string ConnectionString => _container.GetConnectionString();
    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        DataSource = NpgsqlDataSource.Create(ConnectionString);

        // Apply the real migrations once, so tests run against the production schema
        // (including tables created by raw SQL, like PropertyStaging).
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await _container.DisposeAsync();
    }

    public AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;
        return new AppDbContext(options);
    }

    public async Task ResetAsync()
    {
        await using var context = CreateContext();
        await context.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE \"Properties\", \"SourceStates\", \"ImportRuns\", \"PropertyStaging\" RESTART IDENTITY");
    }
}

[CollectionDefinition("Postgres")]
public class PostgresCollectionDefinition : ICollectionFixture<PostgresFixture> { }
