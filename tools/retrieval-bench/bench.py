"""Write-side bench: does the extracted row still contain the answer?

Rescued from a session scratchpad (e2e.py) and rewired. The one change that
matters: prompts are loaded from src/EciCas.Host/instructions/, not from
copies. A bench tuned against a double lets a hand revision to the shipped
file go unmeasured -- the same reason ShippedInstructions exists in the C#
tests.

Scores two things the roadmap folds into one number:
  (a) value sufficiency -- can the question be answered from the rows at all
  (b) pair hit         -- did Librarian open the file the write side used
(a) has never been measured. (b) is the 58% in docs/roadmap.md.
"""
import json, re, sys, urllib.request, collections

ROOT = __file__.rsplit("tools", 1)[0]
INSTR = ROOT + "src/EciCas.Host/instructions/"
sys.path[:0] = [ROOT + "tests/corpora", ROOT + "tools/retrieval-bench"]
import retrieval_v2 as corpus
from answers import ANSWERS

URL = "http://localhost:8080/v1/chat/completions"
MODEL = "qwen3.5-4b"
ROWSPLIT = re.compile(r"(?=\bsubtopic\s*[:=])", re.I)


def load(name):
    """Shipped instruction file -> {section: text}, '#' lines stripped.

    Mirrors IInstructionStore: text before the first '## ' is 'main'.
    """
    out, cur = {}, "main"
    for line in open(INSTR + name, encoding="utf-8"):
        if line.startswith("## "):
            cur = line[3:].strip()
        elif not line.startswith("#"):
            out[cur] = out.get(cur, "") + line
    return {k: v.strip() for k, v in out.items()}


def call(prompt, max_tokens=256):
    # enable_thinking=false mirrors OpenAiCompatibleSubstrateProvider. Without
    # it a Qwen3.5 spends the whole budget in <think> and returns "".
    body = json.dumps({"model": MODEL, "max_tokens": max_tokens,
                       "messages": [{"role": "user", "content": prompt}],
                       "chat_template_kwargs": {"enable_thinking": False}}).encode()
    req = urllib.request.Request(URL, body, {"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=300) as r:
        return json.load(r)["choices"][0]["message"]["content"]


def strip(t):
    return re.sub(r"<think>.*?</think>", "", t, flags=re.S).strip()


# `sentence` is in the marker list because it has to be, not because every
# script wants it: archivist.txt asks for it now, so a splitter that did not
# know the marker would leave the whole sentence sitting inside `value` and
# quietly change what every other bench here measures.
def fields(line):
    """One 'subtopic=.. subject=.. key=.. value=.. sentence=..' line -> dict."""
    d = {}
    for m in re.finditer(r"\b(subtopic|subject|key|value|sentence)\s*[:=]\s*(.*?)(?=\s+\b(?:subtopic|subject|key|value|sentence)\s*[:=]|$)",
                         line, re.I):
        d[m.group(1).lower()] = m.group(2).strip().strip('"').strip()
    return d if {"subject", "key", "value"} <= d.keys() else None


# The three shape anchors use subject=lisbon precisely so a copied example is
# visible. Dropping them here is what ArchivistAgent does at line 196.
COPIED = re.compile(r"\blisbon\b", re.I)


def extract(text, prompt):
    raw = strip(call(prompt.replace("{text}", text)))
    rows = [f for f in (fields(p) for p in ROWSPLIT.split(raw)) if f]
    return [r for r in rows if not COPIED.search(" ".join(r.values()))]


# The sentence restates the fact in full words, so a blob that included it
# would answer nearly every question by construction -- the scorer reading
# the model's own paraphrase back and calling it retrieval. Sufficiency stays
# on the address fields, the same four it has always been scored on.
SCORED = ("subtopic", "subject", "key", "value")


def sufficient(rows, question, answers=None):
    """(a): is every token of some alternative present across the rows?

    answers defaults to this module's v2 key. A caller working the v3
    statement corpus passes answers_v3.ANSWERS -- the two corpora ask
    different questions, and a missing key is a KeyError rather than a miss,
    which is how the mismatch announces itself instead of scoring zero.
    """
    key = answers or ANSWERS
    blob = " ".join(" ".join(r.get(k, "") for k in SCORED) for r in rows).lower()
    return any(all(tok in blob for tok in alt) for alt in key[question])


def score_a(statements, prompt, log=None):
    hit = n = 0
    for stmt, qs in statements:
        rows = extract(stmt, prompt)
        if log is not None:
            log.append((stmt, rows))
        for q in qs:
            n += 1
            hit += sufficient(rows, q)
    return hit, n


def fabrications(prompt):
    """NULLS: any row at all from a message that states nothing."""
    return sum(len(extract(t, prompt)) > 0 for t in corpus.NULLS)


# --- filing: the fatal tier -------------------------------------------------
# A wrong subtopic/subject/key is recoverable, because Recall is handed the
# whole row and can still match on it. A wrong category/topic is not: the file
# name is the index, so Librarian never opens it and the fact is gone.

CAT = load("cataloger.txt")
VOCAB = {l.split(":")[0].strip(): l.split(":")[1].split()
         for l in re.sub(r"\n\s+", " ", CAT["vocabulary"]).splitlines() if ":" in l}


def norm(s):
    return re.sub(r"[^a-z0-9 ]", " ", s.lower())


def file_fact(row, text, vocab=None, cat_prompt=None):
    """Two calls against the closed list, as CatalogerAgent does.

    `vocab` overrides the shipped one so a consolidated vocabulary can be
    filed and read as its own archive -- merging topics changes filing as
    well as selection, so an arm that changed only the read side would be
    measuring a shelf the writer never used.

    `cat_prompt` overrides the DRAWER prompt, and a shelf that renames a
    category must pass it. CAT["category"] is a prose block naming the
    shipped ten by hand: lean survived without this only because it kept
    those ten names, and the first shelf to rename one filed 18 of 31 rows
    to unfiled/unfiled before anyone noticed the vocab override reached the
    topic call alone.
    """
    VOCAB = vocab or globals()["VOCAB"]
    CATP = cat_prompt or CAT["category"]
    fact = " ".join([row.get("subtopic", ""), row["subject"], row["key"], "=", row["value"]])
    raw = norm(strip(call(CATP.replace("{text}", text).replace("{fact}", fact), 24)))
    cat = next((c for c in VOCAB if c in raw), None)
    if cat is None:
        return "unfiled/unfiled"
    raw = norm(strip(call(CAT["topic"].replace("{cat}", cat)
                          .replace("{topics}", "  ".join(VOCAB[cat]))
                          .replace("{text}", text).replace("{fact}", fact), 24)))
    topic = next((t for t in raw.split() if t in VOCAB[cat]), "other")
    return f"{cat}/{topic}"


def write(text, prompt, vocab=None, cat_prompt=None):
    """Full write path: one extraction call, then two filing calls per row."""
    return [(file_fact(r, text, vocab, cat_prompt), r)
            for r in extract(text, prompt)]
