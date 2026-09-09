#!/usr/bin/env python3
"""
Checks that the Vector pipeline parses OpenMU's real log format.

    python3 website/tools/verify-log-parse.py

Exits non-zero and shows the line when anything is off.

WHY THIS EXISTS

The log shipper's regex is the single most fragile part of the log system, and it fails INVISIBLY:
a pattern that does not match does not error, it just files everything under level "Unparsed", and
the absence of a log line looks exactly like nothing having happened. Vector cannot be run in the
build environment, so this checks the pattern the way it can be checked - against real output.

tests/fixtures/serilog-sample.txt is REAL Serilog output, produced by a throwaway program using the
outputTemplate from src/Startup/appsettings.json verbatim. It is not hand-written, and it carries
the cases that broke a guessed pattern:

  * an absent SourceContext and EventId render as EMPTY BRACKETS, "[] []", not as nothing
  * an EventId renders as "{ Id = 42, Name = ItemCreated }" - braces and spaces inside brackets
  * real messages contain brackets: "picked up by player '[GM] Granny' [slot 3]"
  * a message can end in a bracket
  * exceptions span several lines and carry no timestamp of their own

The regex and the multiline patterns are read OUT of vector.yaml rather than copied into this file,
so the thing under test is the thing that ships.
"""
import os
import re
import sys

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..')
VECTOR = os.path.join(ROOT, '..', 'deploy', 'all-in-one', 'vector', 'vector.yaml')
FIXTURE = os.path.join(ROOT, 'tests', 'fixtures', 'serilog-sample.txt')


def patterns():
    """The line regex and the multiline start pattern, taken from vector.yaml itself."""
    config = open(VECTOR, encoding='utf-8').read()

    line = re.search(r"parse_regex\(head, r'(?P<pattern>.+?)'\)", config)
    if not line:
        raise SystemExit('no parse_regex(head, r\'...\') found in vector.yaml')

    start = re.search(r"condition_pattern:\s*'(?P<pattern>.+?)'", config)
    if not start:
        raise SystemExit('no condition_pattern found in vector.yaml')

    return line.group('pattern'), start.group('pattern')


def events(text, start_pattern):
    """Group lines into events the way Vector's multiline halt_before mode does."""
    starts = re.compile(start_pattern)
    grouped, current = [], []
    for raw in text.split('\n'):
        if starts.match(raw) and current:
            grouped.append('\n'.join(current))
            current = []
        current.append(raw)
    if current and any(l.strip() for l in current):
        grouped.append('\n'.join(current))
    return grouped


def parse(event, line_regex):
    """The remap transform, in Python: split off the exception, then match the first line."""
    halves = event.split('\n', 1)
    head = halves[0]
    trace = halves[1].strip() if len(halves) > 1 else ''

    match = re.match(line_regex, head)
    if not match:
        return {'level': 'Unparsed', 'source': None, 'event_id': None,
                'message': event, 'exception': trace or None}

    got = match.groupdict()
    return {
        'at': got['at'],
        'level': got['level'],
        'source': got['source'] or None,
        'event_id': got['event_id'] or None,
        'message': got['message'],
        'exception': trace or None,
    }


# What the fixture MUST produce. Only the interesting fields are asserted; a None means the
# empty-bracket case had to become a null rather than an empty string.
EXPECTED = [
    ('Information', 'MUnique.OpenMU.GameServer.GameServer', None, 'Server listener started.', False),
    ('Information', 'MUnique.OpenMU.GameServer.GameServer', None, 'Starting Server Listener, port 55901', False),
    ('Information', 'MUnique.OpenMU.GameLogic.Player', None, 'Guild created: ["Dragons"], Master: ["Granny"]', False),
    ('Warning', 'MUnique.OpenMU.GameLogic.Player', None, None, False),
    ('Error', 'MUnique.OpenMU.Network.Connection', None, None, False),
    ('Information', 'MUnique.OpenMU.GameLogic.Player', '{ Id = 42, Name = ItemCreated }', 'Crafted item: "Dragon Slayer +9"', False),
    ('Information', None, None, 'Begin starting', False),
    ('Error', 'MUnique.OpenMU.GameLogic.Player', None, None, True),
    ('Information', 'MUnique.OpenMU.GameLogic.Player', None,
     'Item \'"Jewel of Bless"\' got picked up by player \'[GM] Granny\' [slot 3]', False),
    ('Debug', 'MUnique.OpenMU.GameLogic.Player', None, 'A debug line', False),
    ('Fatal', 'MUnique.OpenMU.GameLogic.Player', None, 'A fatal line', False),
    ('Verbose', 'MUnique.OpenMU.GameLogic.Player', None, 'A verbose line', False),
    ('Information', 'MUnique.OpenMU.GameLogic.Player', None, 'A message ending in a bracket ]', False),
    ('Error', 'MUnique.OpenMU.Persistence.EntityFramework.RepositoryProvider', None, 'Could not load account', True),
    ('Information', None, None, 'Finished starting', False),
]

TIMESTAMP = re.compile(r'^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2}$')


def main():
    line_regex, start_pattern = patterns()

    if not os.path.exists(FIXTURE):
        raise SystemExit(f'the fixture is missing: {FIXTURE}\n'
                         "It is named .txt, not .log, because .gitignore's *.log rule excluded it "
                         'once already - and a missing fixture is a silently untested pipeline.')

    text = open(FIXTURE, encoding='utf-8').read()
    grouped = events(text, start_pattern)

    failures = []
    if len(grouped) != len(EXPECTED):
        failures.append(f'{len(grouped)} events grouped but {len(EXPECTED)} expected - '
                        'the multiline pattern is joining or splitting wrongly')

    for index, (event, want) in enumerate(zip(grouped, EXPECTED)):
        got = parse(event, line_regex)
        level, source, event_id, message, has_trace = want
        where = f'event {index + 1}'

        if got['level'] == 'Unparsed':
            failures.append(f'{where}: DID NOT PARSE: {event.splitlines()[0][:110]}')
            continue

        if not TIMESTAMP.match(got['at']):
            failures.append(f'{where}: timestamp not captured cleanly: {got["at"]!r}')
        if got['level'] != level:
            failures.append(f'{where}: level {got["level"]!r}, expected {level!r}')
        if got['source'] != source:
            failures.append(f'{where}: source {got["source"]!r}, expected {source!r}')
        if got['event_id'] != event_id:
            failures.append(f'{where}: event_id {got["event_id"]!r}, expected {event_id!r}')
        if message is not None and got['message'] != message:
            failures.append(f'{where}: message {got["message"]!r}, expected {message!r}')
        if has_trace and not got['exception']:
            failures.append(f'{where}: expected an exception, got none')
        if not has_trace and got['exception']:
            failures.append(f'{where}: unexpected exception {got["exception"]!r}')
        if got['message'] and '\n' in got['message']:
            failures.append(f'{where}: the message swallowed the stack trace')

    # The filter transform drops these before they reach the database.
    dropped = [w[0] for w in EXPECTED if w[0] in ('Debug', 'Verbose')]
    print(f'{len(grouped)} events parsed, {len(dropped)} of them filtered out before the database')

    if failures:
        print()
        for failure in failures:
            print(f'  {failure}')
        print(f'\n{len(failures)} problem(s)')
        return 1

    print('every line in the fixture parses to the fields the table expects')
    return 0


if __name__ == '__main__':
    sys.exit(main())
