"""Join a single Trial00 System Trace's main-thread waits to its own frame clock."""

import argparse
import csv
import json
from bisect import bisect_left
from collections import Counter
from datetime import datetime
from pathlib import Path
from xml.etree import ElementTree


def trace_rows(path):
    cache = {}

    def decode(node):
        if 'ref' in node.attrib:
            return cache[node.get('ref')]
        children = [decode(child) for child in node]
        if node.tag in ('tagged-backtrace', 'backtrace'):
            value = children
        elif node.tag == 'frame':
            value = node.get('name') or node.get('addr', '')
        elif node.tag in ('start-time', 'event-time', 'sample-time', 'duration'):
            value = int(node.text) / 1_000_000
        else:
            value = node.get('fmt') or node.text or ''
        if 'id' in node.attrib:
            cache[node.get('id')] = value
        return value

    for _, node in ElementTree.iterparse(path):
        if node.tag == 'schema':
            columns = [col.findtext('mnemonic') for col in node.findall('col')]
        elif node.tag == 'row':
            yield dict(zip(columns, map(decode, node)))
            node.clear()


def stack_kind(stack):
    text = ' '.join(stack)
    if 'waitUntilSignaledValue' in text:
        return 'metalSharedEvent'
    if 'nextDrawable' in text or 'NextDrawableLocked' in text:
        return 'nextDrawable'
    if 'waitUntilCompleted' in text:
        return 'commandBufferCompletion'
    return 'otherBlocked'


def attribute(start, end, states, stacks, times):
    totals = Counter()
    for row in states:
        when = row['start']
        overlap = min(end, when + row['duration']) - max(start, when)
        if overlap <= 0:
            continue
        kind = row['state']
        if kind == 'Blocked':
            index = bisect_left(times, when)
            near = times[max(0, index - 1):index + 1]
            nearest = min(near, key=lambda stamp: abs(stamp - when)) if near else None
            kind = (stack_kind(stacks[nearest]) if nearest is not None and abs(nearest - when) <= .005
                    else 'unsampledBlocked')
        totals[kind] += overlap
    return {k: round(v, 3) for k, v in totals.items()}


def analyze(directory, toc, states_file, switches_file):
    target = ElementTree.parse(toc).find('./run/info')
    pid = target.find('./target/process').get('pid')
    config = json.loads((directory / 'configuration.json').read_text())
    offset = (datetime.fromisoformat(config['epoch']) - datetime.fromisoformat(
        target.findtext('./summary/start-date'))).total_seconds() * 1000

    def main_thread(row):
        return row['thread'].startswith('Main Thread ') and row['thread'].endswith(f'pid: {pid})')

    states = [row for row in trace_rows(states_file) if main_thread(row)]
    stacks = {r['time']: r['backtrace'] for r in trace_rows(switches_file)
              if main_thread(r) and r['thread-state'] == 'Blocked' and r['backtrace']}
    times = sorted(stacks)
    with (directory / 'frames.csv').open() as stream:
        frames = [r for r in csv.DictReader(stream) if int(r['line']) >= 2 and r['focused'] == 'True']
    if not states or not stacks or not frames:
        raise ValueError('Missing main thread states, context-switch stacks, or focused progression frames')
    states.sort(key=lambda row: row['start'])
    state_ends = [row['start'] + row['duration'] for row in states]
    totals = Counter()
    slow = []
    matched = 0
    for frame in frames:
        end = offset + float(frame['time_ms'])
        gap = float(frame['gap_ms'])
        start = end - gap
        if start < states[0]['start'] or end > state_ends[-1]:
            continue
        first = bisect_left(state_ends, start)
        last = bisect_left(state_ends, end)
        breakdown = attribute(start, end, states[first:last + 1], stacks, times)
        totals.update(breakdown)
        matched += 1
        if gap > 1000 / 60:
            slow.append(dict(line=int(frame['line']), startTraceMs=round(start, 3), gapMs=gap, stateMs=breakdown))
    if not matched:
        raise ValueError('Trace and focused progression do not overlap; use outputs from the same launch')
    return dict(traceOffsetMs=offset, coveredFrames=matched, totalFrames=len(frames),
                stateMs=dict(totals), slowest=sorted(slow, key=lambda r: -r['gapMs'])[:20])


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('directory', 'toc', 'thread_states', 'context_switches'):
        parser.add_argument(name, type=Path)
    args = parser.parse_args()
    print(json.dumps(analyze(args.directory, args.toc, args.thread_states, args.context_switches), indent=2))
