using Microsoft.Extensions.Caching.Memory;
using MuSite.Data;

namespace MuSite.Services;

/// <summary>
/// Caches the public reads, and remembers WHEN each was read.
///
/// The timestamp is not decoration. Character attributes are flushed to the database on
/// SaveProgress - level-up, reset, logout, periodically - not on every tick, so a ranking is
/// already behind the live game before this cache adds anything. Pages show "updated N minutes ago"
/// rather than implying the board is live, which is the difference between a known limitation and a
/// stream of "the site is wrong" reports.
/// </summary>
public sealed class RankingCache(PublicQueries queries, IMemoryCache cache)
{
    private static readonly TimeSpan BoardTtl = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan ProfileTtl = TimeSpan.FromSeconds(60);

    /// <summary>Reads a board page, cached for two minutes per (board, page, search).</summary>
    public Task<Cached<IReadOnlyList<RankingRow>>> GetRankingAsync(RankingBoard board, int offset, int limit, string? search, CancellationToken cancellationToken)
        => this.GetOrAddAsync(
            $"board:{board}:{offset}:{limit}:{search}",
            BoardTtl,
            ct => queries.GetRankingAsync(board, offset, limit, search, ct),
            cancellationToken);

    /// <summary>Counts a board's rows, cached alongside the page.</summary>
    public Task<Cached<int>> CountRankingAsync(RankingBoard board, string? search, CancellationToken cancellationToken)
        => this.GetOrAddAsync(
            $"count:{board}:{search}",
            BoardTtl,
            ct => queries.CountRankingAsync(board, search, ct),
            cancellationToken);

    /// <summary>Reads one character profile, cached for a minute.</summary>
    public Task<Cached<CharacterProfile?>> GetCharacterAsync(string name, CancellationToken cancellationToken)
        => this.GetOrAddAsync(
            $"char:{name.ToLowerInvariant()}",
            ProfileTtl,
            ct => queries.GetCharacterAsync(name, ct),
            cancellationToken);

    /// <summary>Lists guilds, cached for two minutes.</summary>
    public Task<Cached<IReadOnlyList<GuildRow>>> GetGuildsAsync(int offset, int limit, string? search, CancellationToken cancellationToken)
        => this.GetOrAddAsync(
            $"guilds:{offset}:{limit}:{search}",
            BoardTtl,
            ct => queries.GetGuildsAsync(offset, limit, search, ct),
            cancellationToken);

    /// <summary>Reads one guild and its roster, cached for a minute.</summary>
    public Task<Cached<GuildProfile?>> GetGuildAsync(string name, CancellationToken cancellationToken)
        => this.GetOrAddAsync(
            $"guild:{name.ToLowerInvariant()}",
            ProfileTtl,
            ct => queries.GetGuildAsync(name, ct),
            cancellationToken);

    /// <summary>Account and character totals for the home page.</summary>
    public Task<Cached<SiteTotals>> GetTotalsAsync(CancellationToken cancellationToken)
        => this.GetOrAddAsync("totals", BoardTtl, queries.GetTotalsAsync, cancellationToken);

    private async Task<Cached<T>> GetOrAddAsync<T>(string key, TimeSpan ttl, Func<CancellationToken, Task<T>> read, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue<Cached<T>>(key, out var hit) && hit is not null)
        {
            return hit;
        }

        var value = new Cached<T>(await read(cancellationToken).ConfigureAwait(false), DateTimeOffset.UtcNow);
        cache.Set(key, value, ttl);
        return value;
    }
}

/// <summary>A cached value and the moment it was read from the database.</summary>
/// <typeparam name="T">The cached type.</typeparam>
/// <param name="Value">The value.</param>
/// <param name="AsOf">When it was read.</param>
public sealed record Cached<T>(T Value, DateTimeOffset AsOf)
{
    /// <summary>A short "updated N ago" phrase for the page.</summary>
    public string Age
    {
        get
        {
            var elapsed = DateTimeOffset.UtcNow - this.AsOf;
            return elapsed.TotalSeconds < 90
                ? "moments ago"
                : $"{(int)elapsed.TotalMinutes} min ago";
        }
    }
}
