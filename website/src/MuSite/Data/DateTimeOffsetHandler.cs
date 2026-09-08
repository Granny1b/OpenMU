using System.Data;
using Dapper;

namespace MuSite.Data;

/// <summary>
/// Lets Dapper materialise a <see cref="DateTimeOffset"/> from a PostgreSQL timestamptz column.
///
/// Npgsql 6 changed the default CLR mapping for `timestamp with time zone` from DateTimeOffset to
/// DateTime (Kind=Utc), so DbDataReader.GetFieldType reports System.DateTime. Dapper matches a
/// record's constructor by comparing those reported types to the parameter types PAIRWISE, and
/// rejects the constructor outright when one differs:
///
///   System.InvalidOperationException: A parameterless default constructor or one matching
///   signature (... System.DateTime createdat, System.DateTime publishedat) is required for
///   MuSite.Services.NewsItem materialization
///
/// It surfaces on the first row read, not at compile time and not on an empty result - which is why
/// an empty news table hid it until the first announcement existed.
///
/// Registering a handler for DateTimeOffset makes Dapper tolerate the difference: the type check in
/// DefaultTypeMap.FindConstructor is skipped when SqlMapper.HasTypeHandler(type) is true, and the
/// conversion is routed here. Dapper resolves DateTimeOffset? through this same handler, mapping a
/// NULL column to null.
///
/// The alternative - redeclaring every such property as DateTime - would spread Kind-sensitive
/// values through code that uses DateTimeOffset consistently everywhere else (SessionStore,
/// AdminActions, RankingCache), where a stray .ToLocalTime() would go unnoticed.
/// </summary>
public sealed class DateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset>
{
    /// <summary>Converts what Npgsql read into a <see cref="DateTimeOffset"/>.</summary>
    public override DateTimeOffset Parse(object value) => value switch
    {
        DateTimeOffset already => already,

        // Npgsql returns Kind=Utc for timestamptz. Local is converted rather than reinterpreted:
        // treating a local time as UTC would silently shift it by the machine's offset.
        DateTime { Kind: DateTimeKind.Utc } utc => new DateTimeOffset(utc, TimeSpan.Zero),
        DateTime { Kind: DateTimeKind.Local } local => new DateTimeOffset(local).ToUniversalTime(),

        // Unspecified: every timestamp column in db/web/001_init.sql is timestamptz, so an
        // unspecified value can only come from a `timestamp without time zone` added later. This
        // schema stores UTC throughout, so read it as UTC.
        DateTime unspecified => new DateTimeOffset(
            DateTime.SpecifyKind(unspecified, DateTimeKind.Utc), TimeSpan.Zero),

        _ => throw new InvalidCastException(
            $"Cannot read {value?.GetType().FullName ?? "null"} as DateTimeOffset."),
    };

    /// <summary>Writes a <see cref="DateTimeOffset"/> parameter.</summary>
    public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
    {
        // UtcDateTime, not the DateTimeOffset itself: Npgsql rejects a DateTimeOffset with a
        // non-zero offset when writing to timestamptz. Every value the site writes today is derived
        // from DateTimeOffset.UtcNow and would pass either way, but this cannot be the thing that
        // breaks when a caller some day passes a local-offset value.
        parameter.Value = value.UtcDateTime;
    }
}
