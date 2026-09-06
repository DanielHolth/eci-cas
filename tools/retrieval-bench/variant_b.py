"""Variant B: key is a property of the subject, and a third person is named.

Baseline's two misses are both subject-side. "My brother Lars lives in Tromso"
gives subject=brother, and once the relation is the subject the name has no
slot left, so "Lars" is dropped. "I studied geology at NTNU" gives
subject=user key=degree, and NTNU has nowhere to go.

The shipped file defines exactly one field -- subject -- and says nothing
about what a key is. Naming key as a property of subject constrains subject
transitively: a colour belongs to a car, a location belongs to Lars.

Both added lines stay templated. The file's own comment explains why: a
literal worked example gets harvested as a fact, and it cost the persona its
name once. The three shape anchors use subject=lisbon for the same reason.
"""
ANCHOR = """subject is who or what the fact is about. It is never a restatement of the value."""

PROPERTY = """subject is who or what the fact is about. It is never a restatement of the value.

key is a property of subject. When the key names something that belongs to
some other thing, that thing is the subject: the colour of a car has
subject=car, not subject=user.

A fact about another person has that person as the subject, under the name
they were given, not under the relation:
subtopic=<1-2 words> subject=<their name> key=<1-3 words> value=<1-4 keywords>"""


def apply(main):
    assert ANCHOR in main, "shipped archivist.txt no longer has the subject line"
    return main.replace(ANCHOR, PROPERTY)
