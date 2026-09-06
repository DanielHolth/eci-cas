"""Extraction A/B, scored on both axes at once.

The filing bench keeps saying the same thing from different directions: the
drawer is usually right, and the row handed to it is usually what went wrong.
"I cannot eat shellfish" comes out as `dietary restriction user food
preference = shellfish` and is then filed under identity -- correctly, for
the row it was given. A preference is an identity fact. An allergy is not.
The Cataloger is answering the question it was asked.

So both arms are scored on both things, because an extraction rule that
improves one and wrecks the other is not an improvement:

  value   can the question still be answered from the rows      (answers.py)
  drawer  is the category defensible for the statement          (filing_key.py)
  nulls   rows produced from messages that state nothing        (fabrication)

RESULT: property shipped -- value 81% -> 91%, better or equal in 5 of 5 and
better in 4, drawer unchanged, and fabrication on the NULLs 0.4 rows per rep
down to 0.0. literal was not shipped: it wins three reps, loses one and ties
one, which is what the noise floor produces on its own. The arms below are
therefore read the other way round -- "property" is what archivist.txt now
says, and "bare" is the old line reconstructed to keep this re-runnable.

Arms:
  bare      archivist.txt with the property rule taken back out
  property  shipped: key is a property of subject, and a fact about another
            person is filed under that person's name
  literal   property, plus: the key names what was stated, not what it means.
            The untested half of the original diagnosis -- "food preference"
            for a medical restriction is the model interpreting, and the
            interpretation is what misfiles the row.

  python extract_ab.py [reps]
"""
import sys
import bench
from bench import extract, sufficient, VOCAB, corpus
from review_other import pick, as_fact
from cat_tie import category
from filing_key import KEY
import variant_b

LITERAL = """key is a property of subject. When the key names something that belongs to
some other thing, that thing is the subject: the colour of a car has
subject=car, not subject=user.

The key names what was stated, not what it means. Someone who says they
cannot eat something has stated a restriction, not a preference; someone who
names a date has stated that date, not a plan. Do not translate the fact into
what it implies about the person."""

CATS = {stmt: {p.split("/")[0] for p in pairs} for stmt, pairs in KEY.items()}


def arms():
    prop = bench.load("archivist.txt")["main"]
    return {
        "bare": variant_b.apply(prop),
        "property": prop,
        "literal": prop.replace(variant_b.PROPERTY.split("\n\n")[1], LITERAL),
    }


def run(reps):
    built = arms()
    assert built["literal"] != built["property"], "LITERAL did not splice in"
    tally = {k: [] for k in built}
    for rep in range(reps):
        for name, prompt in built.items():
            vhit = vn = dhit = dn = 0
            for stmt, qs in corpus.DEV:
                rows = extract(stmt, prompt)
                for q in qs:
                    vn += 1
                    vhit += sufficient(rows, q)
                for row in rows:
                    dn += 1
                    dhit += category(as_fact(row), stmt, bench.CAT["category"]) in CATS[stmt]
            nulls = sum(len(extract(t, prompt)) > 0 for t in corpus.NULLS)
            tally[name].append((vhit / vn, dhit / dn, nulls))
            print(f"rep{rep+1} {name:9} value {vhit}/{vn} = {vhit/vn:.0%}"
                  f"   drawer {dhit}/{dn} = {dhit/dn:.0%}   nulls {nulls}/{len(corpus.NULLS)}")
    print()
    for name, r in tally.items():
        print(f"{name:9} value {sum(v for v, _, _ in r)/len(r):.0%}"
              f"   drawer {sum(d for _, d, _ in r)/len(r):.0%}"
              f"   nulls/rep {sum(n for _, _, n in r)/len(r):.1f}")


if __name__ == "__main__":
    run(int(sys.argv[1]) if len(sys.argv) > 1 else 4)
