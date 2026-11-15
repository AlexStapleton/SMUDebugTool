"""Summarize repeatable CPU-free probes; retain ranges instead of hiding variance."""
import argparse
import json
import pathlib
import statistics

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--before", nargs="+", required=True)
parser.add_argument("--after", nargs="+", required=True)
parser.add_argument("--export-before")
parser.add_argument("--export-after")
parser.add_argument("--output", required=True)
args = parser.parse_args()


def read(path):
    return {row["Name"]: row for row in json.loads(pathlib.Path(path).read_text(encoding="utf-8-sig"))["Results"]}


before, after = [read(p) for p in args.before], [read(p) for p in args.after]
if any(set(run) != set(before[0]) for run in before + after):
    raise SystemExit("Before/after fixtures must contain identical operation names.")
results = []
for name in before[0]:
    b = [run[name]["MicrosecondsPerOperation"] for run in before]
    a = [run[name]["MicrosecondsPerOperation"] for run in after]
    results.append(dict(Name=name, BeforeUs=statistics.median(b), AfterUs=statistics.median(a),
                        BeforeRange=b, AfterRange=a,
                        BeforeAllocatedBytes=statistics.median(run[name]["AllocatedBytesPerOperation"] for run in before),
                        AfterAllocatedBytes=statistics.median(run[name]["AllocatedBytesPerOperation"] for run in after)))
if args.export_before and args.export_after:
    eb, ea = read(args.export_before), read(args.export_after)
    if set(eb) != set(ea):
        raise SystemExit("Export operation names differ.")
    for name in eb:
        results.append(dict(Name=name, BeforeUs=eb[name]["MicrosecondsPerOperation"], AfterUs=ea[name]["MicrosecondsPerOperation"],
                            BeforeAllocatedBytes=eb[name]["AllocatedBytesPerOperation"], AfterAllocatedBytes=ea[name]["AllocatedBytesPerOperation"], Repetitions=1))
pathlib.Path(args.output).write_text(json.dumps(dict(SchemaVersion=1,
    Notes="Median of independent sequential process runs; ranges retained. Export: five measured operations after eight warm-ups in one process per build. Synthetic software costs, not whole-app or driver performance. Allocation is approximate process-wide cumulative bytes, not resident memory.",
    Results=results), indent=2), encoding="utf-8")
