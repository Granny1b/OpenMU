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
    # DateTimeOffsetHandler so both are accepted; see Data/DateTimeOffsetHandler.cs.
    'timestamp with time zone': 'DateTime',
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
    '@mapNumber': '8',
    '@search': "''",
    '@group': 'NULL::int',
    '@limit': '50',
    '@number': '13',
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
    """[(sql, record name)] in file order."""
    sqls = re.findall(r'const string sql\s*=\s*"""(.*?)""";', src, re.S)
    types = re.findall(r'Query(?:SingleOrDefault)?Async<(\w+)>', src)
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


def main():
    connection = os.environ.get('MUSITE_TEST_DB')
    if not connection:
        print('set MUSITE_TEST_DB to a libpq connection string', file=sys.stderr)
        return 2

    # Npgsql keywords are not libpq keywords; translate the few that differ.
    libpq = (connection.replace('Host=', 'host=').replace('Port=', 'port=')
             .replace('Username=', 'user=').replace('Password=', 'password=')
             .replace('Database=', 'dbname=').replace(';', ' '))

    path = os.path.join(ROOT, 'src', 'MuSite', 'Data', 'GameCatalog.cs')
    src = open(path, encoding='utf-8').read()
    known = records(src)

    failures = 0
    for index, (sql, record) in enumerate(queries(src)):
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
            elif ptype.rstrip('?') != want:
                problems.append(
                    f'TYPE: {pname} declared {ptype} but {cname} is {ctype}, '
                    f'which Npgsql reports as {want}')

        print(f'{record:<14} {"ok" if not problems else "FAIL"}')
        for problem in problems:
            print(f'      {problem}')
        failures += bool(problems)

    print()
    print('every query maps' if not failures else f'{failures} query/record mismatches')
    return 0 if not failures else 1


if __name__ == '__main__':
    sys.exit(main())
