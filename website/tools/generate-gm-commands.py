#!/usr/bin/env python3
"""
Regenerates website/src/MuSite/Game/GmCommands.Generated.cs from the OpenMU server source.

The website deliberately has NO ProjectReference into ../src (CI fails the build if one appears),
so the command catalogue cannot be reflected at runtime the way OpenMU's own admin panel does. It
is generated here and committed instead.

Re-run after pulling upstream:

    python3 website/tools/generate-gm-commands.py

Sources of truth, all under src/GameLogic:
  PlugIns/ChatCommands/*.cs            the command string, required status, disabled-by-default
  PlugIns/ChatCommands/Arguments/*.cs  the argument properties
  Properties/PlugInResources.resx      the descriptions
"""
import glob
import os
import re
import sys
import xml.etree.ElementTree as ET

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..')
GL = os.path.join(ROOT, 'src', 'GameLogic')
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                   '..', 'src', 'MuSite', 'Game', 'GmCommands.Generated.cs')

CS_TO_KIND = {
    'byte': 'Integer', 'short': 'Integer', 'int': 'Integer', 'long': 'Integer',
    'uint': 'Integer', 'ushort': 'Integer', 'sbyte': 'Integer',
    'bool': 'Flag',
    'string': 'Text', 'string?': 'Text',
}


def read(path):
    return open(path, encoding='utf-8-sig').read()


def descriptions():
    resx = os.path.join(GL, 'Properties', 'PlugInResources.resx')
    if not os.path.exists(resx):
        return {}
    root = ET.parse(resx).getroot()
    return {d.get('name'): (d.findtext('value') or '').strip() for d in root.iter('data')}


def class_bodies(src):
    """Every `class X : ArgumentsBase` in a file, mapped to its body, by brace counting.

    A regex cannot delimit a C# class body: the earlier version of this generator used one and
    silently truncated ItemChatCommandArgs, losing the `anc` and `ancBonuslvl` arguments. A
    generator that drops arguments without complaining is worse than no generator.
    """
    bodies = {}
    for m in re.finditer(r'class (\w+)\s*:\s*ArgumentsBase\b[^{]*\{', src):
        name = m.group(1)
        depth, i = 1, m.end()
        while i < len(src) and depth:
            if src[i] == '{':
                depth += 1
            elif src[i] == '}':
                depth -= 1
            i += 1
        bodies[name] = src[m.end():i - 1]
    return bodies


def parse_properties(body):
    """The argument properties of one args class, in declaration order.

    Walks the properties and reads the attribute block that precedes each one, rather than
    requiring [Argument] to sit immediately above `public`. An earlier version required that
    adjacency and silently lost /item's `anc` and `ancBonuslvl`, because [ValidValues] sits
    between the two. Any attribute may intervene, and more may be added upstream.

    Properties WITHOUT [Argument] are kept, marked named=False: the parser
    (GameLogic/PlugIns/ChatCommands/CommandExtensions.cs) only matches names for attributed
    properties, but every settable property still participates positionally. /setmoney and the
    whole /set* family have no attributes at all, so dropping them would report those commands as
    taking no arguments.
    """
    props = []
    prev_end = 0
    for m in re.finditer(r'public\s+([\w?<>]+)\s+(\w+)\s*\{\s*get;\s*set;\s*\}', body):
        cstype, prop = m.group(1), m.group(2)
        attrs = body[prev_end:m.start()]
        prev_end = m.end()

        # Only the attribute block for THIS property: anything before a blank line belongs to the
        # previous member or to the class.
        attrs = attrs.rsplit('\n\n', 1)[-1]

        arg = re.search(r'\[Argument\("([^"]+)"(?:\s*,\s*(true|false))?\)\]', attrs)
        valid = re.search(r'\[ValidValues\(([^)]*)\)\]', attrs)
        props.append({
            'name': prop,
            'short': arg.group(1) if arg else '',
            'named': arg is not None,
            'required': (arg.group(2) != 'false') if arg else False,
            'kind': CS_TO_KIND.get(cstype, 'Text'),
            'valid': re.findall(r'"([^"]*)"', valid.group(1)) if valid else [],
        })
    return props


def shared_argument_classes():
    """Args classes under Arguments/, which are referenced across files by their own name."""
    classes = {}
    for path in glob.glob(os.path.join(GL, 'PlugIns', 'ChatCommands', 'Arguments', '*.cs')):
        for name, body in class_bodies(read(path)).items():
            classes[name] = parse_properties(body)
    return classes


def resolve_args(src, type_name, shared):
    """Nested class in this file wins over the shared Arguments/ directory."""
    local = class_bodies(src)
    if type_name in local:
        return parse_properties(local[type_name])
    return shared.get(type_name, [])


def commands(shared, desc):
    out = []
    files = glob.glob(os.path.join(GL, 'PlugIns', 'ChatCommands', '*.cs'))
    files += glob.glob(os.path.join(GL, 'Resets', '*ChatCommand*.cs'))
    for path in files:
        src = read(path)
        cmd = (re.search(r'private const string Command\s*=\s*"([^"]+)"', src)
               or re.search(r'public override string Key\s*=>\s*"([^"]+)"', src))
        if not cmd:
            continue
        status = (re.search(r'private const CharacterStatus MinimumStatus\s*=\s*CharacterStatus\.(\w+)', src)
                  or re.search(r'MinCharacterStatusRequirement\s*=>\s*CharacterStatus\.(\w+)', src))
        argtype = re.search(r'\[ChatCommandHelp\([^\]]*typeof\((?:[\w.]+\.)?(\w+)\)', src, re.S)
        stem = os.path.basename(path)[:-3]

        # No default. Every command in the tree today declares its status explicitly, one of two
        # ways, and both are matched above. If a third way appears upstream, this must stop the
        # generator rather than quietly emit Normal - a command reported as needing less than it
        # does is the one kind of error this catalogue must never make.
        if not status:
            raise SystemExit(
                f'{os.path.basename(path)}: cannot find the CharacterStatus for {cmd.group(1)}. '
                f'Teach the generator the new declaration pattern rather than guessing.')

        out.append({
            'command': cmd.group(1),
            'status': status.group(1),
            'disabled': 'IDisabledByDefault' in src,
            'args': resolve_args(src, argtype.group(1), shared) if argtype else [],
            'description': desc.get(f'{stem}_Description', ''),
            'source': os.path.basename(path),
        })
    out.sort(key=lambda c: c['command'])
    return out


def escape(text):
    return text.replace('\\', '\\\\').replace('"', '\\"')


def emit(cmds):
    lines = [
        '// <auto-generated>',
        '//     Generated by website/tools/generate-gm-commands.py from the OpenMU server source.',
        '//     DO NOT EDIT BY HAND - re-run the generator after pulling upstream.',
        '//',
        '//     The website has no ProjectReference into ../src (CI fails the build if one appears),',
        '//     so this catalogue cannot be reflected at runtime the way OpenMU\'s own admin panel',
        '//     does. It is generated and committed instead.',
        '// </auto-generated>',
        '',
        'namespace MuSite.Game;',
        '',
        '/// <content>The generated command catalogue.</content>',
        'public static partial class GmCommands',
        '{',
        '    /// <summary>Every chat command OpenMU ships, newest generation.</summary>',
        '    public static readonly IReadOnlyList<GmCommand> All =',
        '    [',
    ]
    for c in cmds:
        args = ', '.join(
            'new("{name}", "{short}", GmArgKind.{kind}, {named}, {req}, [{valid}])'.format(
                name=escape(a['name']), short=escape(a['short']), kind=a['kind'],
                named='true' if a['named'] else 'false',
                req='true' if a['required'] else 'false',
                valid=', '.join('"%s"' % escape(v) for v in a['valid']))
            for a in c['args'])
        lines.append('        new("{cmd}", GmStatus.{st}, {dis}, "{desc}", [{args}]),'.format(
            cmd=escape(c['command']), st=c['status'],
            dis='true' if c['disabled'] else 'false',
            desc=escape(c['description']), args=args))
    lines += ['    ];', '}', '']
    return '\n'.join(lines)


def main():
    desc = descriptions()
    cmds = commands(shared_argument_classes(), desc)
    if len(cmds) < 50:
        print(f'refusing to write: only {len(cmds)} commands found, expected 60+', file=sys.stderr)
        return 1
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    open(OUT, 'w', encoding='utf-8').write(emit(cmds))
    withargs = sum(1 for c in cmds if c['args'])
    named = sum(1 for c in cmds if any(a['named'] for a in c['args']))
    valid = sum(len([a for a in c['args'] if a['valid']]) for c in cmds)
    withdesc = sum(1 for c in cmds if c['description'])
    print(f'{len(cmds)} commands -> {os.path.relpath(OUT, ROOT)}')
    print(f'   {sum(1 for c in cmds if c["status"] == "GameMaster")} GameMaster, '
          f'{sum(1 for c in cmds if c["disabled"])} disabled by default')
    print(f'   {withargs} with arguments ({named} addressable by name), {withdesc} with descriptions')
    print(f'   {sum(len(c["args"]) for c in cmds)} arguments total, {valid} with a ValidValues list')
    return 0


if __name__ == '__main__':
    sys.exit(main())
