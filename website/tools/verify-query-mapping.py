#!/usr/bin/env python3
"""
Checks that every Dapper query's columns line up with its record's constructor - by NAME, in ORDER,
and by TYPE - against a live PostgreSQL.

    MUSITE_TEST_DB="Host=localhost;Username=postgres;Password=...;Database=openmu" \
        python3 website/tools/verify-query-mapping.py

Exits non-zero and names the mismatch when anything is off.

WHY THIS EXISTS

Dapper matches a record's constructor to the reader's columns PAIRWISE - it compares
ctorParameters[i].Name to names[i], and the parameter's type to the type the reader REPORTS. Any
mismatch and it rejects the constructor outright, at the first row read:

    System.InvalidOperationException: A parameterless default constructor or one matching
    signature (...) is required for X materialization

Nothing catches it at compile time, and an empty result set never triggers it. Three separate
failures shipped this way:

  * CharacterProfile - the SELECT listed all seventeen columns but three in the wrong order, so
    every character page threw while a lookup for a character that did not exist returned cleanly.
  * NewsItem, AuditEntry, BanRecord - declared DateTimeOffset against timestamptz, which Npgsql 6
    reports as DateTime. The home page threw as soon as one announcement existed.
  * ItemRow - declared int against smallint. Every C# `byte` in OpenMU is smallint in PostgreSQL,
    which Npgsql reports as Int16, so the intuitive declaration is the wrong one.

An earlier version of this script compared names and order only. It passed the ItemRow queries the
day they were written, because the bug was in the types - which is why it now checks all three.

WHAT IT CANNOT DO

It reads the SQL out of the source with a regex, so it only sees queries assigned to a
`const string sql` raw-string literal with a `Query*Async<Record>` call nearby, and it has to be
told what to substitute for each parameter. It is a targeted check over MuSite.Data, not a general
tool.
"""
import os
import re
import subprocess
import sys

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..')

# What Npgsql reports for a PostgreSQL type, and therefore what the record must declare.
EXPECTED_CLR = {
    'smallint': 'short',
    'integer': 'int',
    'bigint': 'long',
    'real': 'float',
    'double precision': 'double',
    'numeric': 'decimal',
    'boolean': 'bool',
    'text': 'string',
    'uuid': 'Guid',
    'inet': 'IPAddress',

    # Npgsql 6 changed this one from DateTimeOffset to DateTime. MuSite registers a
    # DateTimeOffsetHandler so BOTH really do work; see Data/DateTimeOffsetHandler.cs. A value may
    # be a tuple when more than one declaration is genuinely correct.
    'timestamp with time zone': ('DateTime', 'DateTimeOffset'),
}

# Stand-ins for query parameters. A new parameter needs an entry here or its query is skipped
# loudly rather than silently passed.
PARAMETERS = {
    '@levelId': "'560931AD-0901-4342-B7F4-FD2E2FCC0563'::uuid",
    '@healthId': "'A6C39A5C-295F-415E-A314-5E9F9A748D27'::uuid",
    '@minDmgId': "'3E8D6A02-E973-4AE4-9DF3-CDDC3D3183B3'::uuid",
    '@maxDmgId': "'8A918EA2-893A-48B2-A684-3E71526CA71F'::uuid",
    '@defenseId': "'EB098C46-60D4-4CA6-BBD4-5B6270A1407B'::uuid",
    '@monsterNumber': '78',
    '@search': "''",
    '@group': 'NULL::int',
    '@limit': '50',
    '@offset': '0',
    '@number': '13',

    # ItemOptionTypes.Excellent and ItemOptionTypes.Option, which is what tells an item's
    # bit-field options apart from its levelled one.
    '@excellentType': "'6487C498-58E0-48E5-B409-35D7598313FC'::uuid",
    '@optionType': "'F193F91E-86D7-4456-ADD8-A3667E731303'::uuid",

    # ServerLog, over the site's own database.
    '@hours': '24',
    '@level': "''",
    '@source': "''",
}


def records(src):
    """Record name -> ordered [(parameter name, declared type)]."""
    found = {}
    for m in re.finditer(r'public sealed record (\w+)\(([^;]*?)\);', src, re.S):
        params = []
        for part in m.group(2).replace('\n', ' ').split(','):
            words = part.strip().split()
            if len(words) < 2:
                continue
            params.append((words[-1].strip('"'), ' '.join(words[:-1])))
        found[m.group(1)] = params
    return found


def queries(src):
    """[(sql, record name)] in file order.

    Pairing is POSITIONAL: the Nth `const string sql` goes with the Nth Query*Async<T>. That holds
    only while every such literal has exactly one matching call, so the counts are asserted rather
    than zipped blindly - zip() silently truncates, which would check some queries against another
    query's record and report "ok".

    Scalar queries are deliberately outside this: name their literal `countSql` (not `sql`) and
    they are skipped. A COUNT(*) has no record to line up with.
    """
    sqls = re.findall(r'const string sql\s*=\s*"""(.*?)""";', src, re.S)
    types = re.findall(r'Query(?:SingleOrDefault)?Async<(\w+)>', src)
    if len(sqls) != len(types):
        raise SystemExit(
            f'{len(sqls)} `const string sql` literals but {len(types)} Query*Async<T> calls. '
            'Positional pairing is broken - a scalar query should use `const string countSql`.')
    return list(zip(sqls, types))


def columns(sql, index, connection):
    """Ask PostgreSQL what the query's columns are actually called and typed."""
    for name, stand_in in sorted(PARAMETERS.items(), key=lambda kv: -len(kv[0])):
        sql = sql.replace(name, stand_in)

    if '@' in re.sub(r"'[^']*'", '', sql):
        unknown = set(re.findall(r'@\w+', sql))
        raise KeyError(f'no stand-in for {", ".join(sorted(unknown))}')

    script = (f'CREATE TEMP VIEW v{index} AS {sql};\n'
              f"SELECT column_name || '|' || data_type FROM information_schema.columns "
              f"WHERE table_name='v{index}' ORDER BY ordinal_position;\n")
    result = subprocess.run(
        ['psql', connection, '-tAq', '-v', 'ON_ERROR_STOP=1', '-f', '-'],
        input=script, capture_output=True, text=True)
    if result.returncode:
        raise RuntimeError(result.stderr.strip()[:300])

    return [line.split('|') for line in result.stdout.strip().split('\n') if line]


# Each file is checked against the database its queries actually run on. GameCatalog reads the
# game's schema as mu_web_read; ServerLog reads the site's own openmu_web.
TARGETS = [
    (os.path.join('src', 'MuSite', 'Data', 'GameCatalog.cs'), 'MUSITE_TEST_DB'),
    (os.path.join('src', 'MuSite', 'Services', 'ServerLog.cs'), 'MUSITE_TEST_SITE_DB'),
]


def libpq_of(connection):
    """Npgsql keywords are not libpq keywords; translate the few that differ."""
    return (connection.replace('Host=', 'host=').replace('Port=', 'port=')
            .replace('Username=', 'user=').replace('Password=', 'password=')
            .replace('Database=', 'dbname=').replace(';', ' '))


def check(path, connection, offset):
    """Returns (failures, queries checked)."""
    src = open(os.path.join(ROOT, path), encoding='utf-8').read()
    known = records(src)
    libpq = libpq_of(connection)

    failures = 0
    pairs = queries(src)
    for position, (sql, record) in enumerate(pairs):
        index = offset + position
        if record not in known:
            print(f'{record:<14} SKIPPED: no record definition found in this file')
            failures += 1
            continue
        try:
            actual = columns(sql, index, libpq)
        except (KeyError, RuntimeError) as error:
            print(f'{record:<14} FAILED: {error}')
            failures += 1
            continue

        problems = []
        declared = known[record]
        if len(declared) != len(actual):
            problems.append(f'{len(declared)} parameters vs {len(actual)} columns')

        for (pname, ptype), (cname, ctype) in zip(declared, actual):
            if pname.lower() != cname.lower():
                problems.append(f'ORDER: parameter {pname} meets column {cname}')
            want = EXPECTED_CLR.get(ctype)
            if want is None:
                problems.append(f'UNKNOWN TYPE: {cname} is {ctype}; add it to EXPECTED_CLR')
            else:
                allowed = want if isinstance(want, tuple) else (want,)
                if ptype.rstrip('?') not in allowed:
                    problems.append(
                        f'TYPE: {pname} declared {ptype} but {cname} is {ctype}, '
                        f'which Npgsql reports as {" or ".join(allowed)}')

        print(f'  {record:<20} {"ok" if not problems else "FAIL"}')
        for problem in problems:
            print(f'      {problem}')
        failures += bool(problems)

    return failures, len(pairs)


def main():
    failures = 0
    checked = 0

    for path, variable in TARGETS:
        connection = os.environ.get(variable)
        if not connection:
            print(f'{path}: SKIPPED - {variable} is not set')
            failures += 1
            continue

        print(f'{path}  ({variable})')
        # The temp-view names are numbered across all targets so two files cannot collide.
        problems, count = check(path, connection, checked)
        failures += problems
        checked += count

    print()
    print(f'every one of {checked} queries maps' if not failures
          else f'{failures} query/record mismatch(es)')
    return 0 if not failures else 1


if __name__ == '__main__':
    sys.exit(main())
