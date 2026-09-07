"""The whole address, as written, statement by statement.

Every other script here scores one segment. This one prints what actually
lands: category/topic/subtopic/subject/key = value, for every row, across
several runs of the real write path (one extraction call, then the two filing
calls per row). No key, no score -- the point is to read it.

The right-hand column is how many runs produced that exact path, so a row
that only appears once is drift and a row that appears every time is settled.

  python full_path.py [runs]
"""
import collections, sys
import bench


def run(n):
    prompt = bench.load("archivist.txt")["main"]
    for stmt, _ in bench.corpus.DEV:
        seen = collections.Counter()
        vals = {}
        for _ in range(n):
            for pair, row in bench.write(stmt, prompt):
                path = f"{pair}/{row.get('subtopic','-')}/{row['subject']}/{row['key']}"
                seen[path.lower()] += 1
                vals.setdefault(path.lower(), row["value"])
        print(f"\n{stmt}")
        for path, count in seen.most_common():
            print(f"  {count}/{n}  {path} = {vals[path]}")


if __name__ == "__main__":
    run(int(sys.argv[1]) if len(sys.argv) > 1 else 3)
