"""The embedder, standalone. No server, no API, no tier.

    from embed import Embedder
    e = Embedder()
    v = e.encode(["Daniel's passport expires in March 2027"])   # (1, 384)
    e.cosine(v[0], v[0])                                        # 1.0

bge-small-en-v1.5 through onnxruntime on CPU: 133MB on disk, 384 dimensions,
milliseconds a row. Cheaper than one Archivist call, which is why the
roadmap says the embedding is not a tier feature -- the same vectors serve
Minimal and Default, and an archive embedded once is readable by every tier.
That is also what makes the cross arm honest later: one tier's readers over
another tier's archive compare identical vectors, so any difference is the
reader and nothing else.

Two things deliberately not hidden.

**English only.** The corpus is English so this is correct for measuring,
but archivist.txt writes the sentence in the language of the message and a
real archive will not be. multilingual-e5-small is the likely ship model --
same 384 dims, larger file -- and swapping it is what the ModelId stamp
exists for. Which embedder is itself a cheap arm once this works, so the
model name is a parameter and appears in MODEL_ID rather than being assumed.

**Mean pooling with the attention mask, then L2 normalise.** BERT-family
retrieval models are trained this way and a naive mean over padding tokens
quietly degrades every vector in a batch containing one short row. Once
normalised, cosine is a dot product, which is the only reason a whole-file
sweep is arithmetic rather than a cost.
"""
import os
import numpy as np

ROOT = __file__.rsplit("tools", 1)[0]
# multilingual-e5-small, not bge-small-en-v1.5, though bge is 4-6pp better on
# the all-English v4 corpus and faster. archivist.txt writes the sentence in
# the language of the message, and lang_v4 shows bge cannot find a Norwegian
# row from an English question at any k (mean rank 256 of 1579, against e5's
# 4.5). Override with ECI_EMBED to measure the other one.
DEFAULT_MODEL = os.environ.get(
    "ECI_EMBED", ROOT + "models/embedding/multilingual-e5-small")


class Embedder:
    def __init__(self, path=None, max_len=256):
        import onnxruntime
        from tokenizers import Tokenizer

        self.path = path or DEFAULT_MODEL
        if not os.path.exists(self.path):
            raise SystemExit(
                "no embedding model at %s -- see README, v4" % self.path)
        self.name = os.path.basename(self.path.rstrip("/\\"))
        self.max_len = max_len
        self.tok = Tokenizer.from_file(os.path.join(self.path, "tokenizer.json"))
        self.tok.enable_truncation(max_length=max_len)
        self.tok.enable_padding()
        opts = onnxruntime.SessionOptions()
        opts.log_severity_level = 3
        self.sess = onnxruntime.InferenceSession(
            os.path.join(self.path, "model.onnx"), opts,
            providers=["CPUExecutionProvider"])
        self.inputs = set(i.name for i in self.sess.get_inputs())
        self.prefix = ({"query": "query: ", "passage": "passage: "}
                       if "e5" in self.name else {})

    @property
    def model_id(self):
        """What gets stamped beside a vector. A vector whose model is not the
        current one is not comparable to a fresh question vector, and the
        read path treats it as missing rather than as data."""
        return "%s/%d" % (self.name, self.max_len)

    def encode(self, texts, batch=64, kind=None):
        """`kind` is "query" or "passage" for models trained asymmetrically.

        The e5 family is: it was trained with "query: " and "passage: "
        prefixes and loses several points without them, because the two roles
        occupy different regions of its space by design. bge-small-en has no
        such prefix in this usage, so the mapping below is per-model rather
        than a flag the caller has to remember -- passing kind to a model that
        does not want one is a no-op, which keeps every arm's call identical
        across embedders.
        """
        pre = self.prefix.get(kind, "") if kind else ""
        if pre:
            texts = [pre + t for t in texts]
        out = []
        for i in range(0, len(texts), batch):
            chunk = texts[i:i + batch]
            enc = self.tok.encode_batch(chunk)
            ids = np.array([e.ids for e in enc], dtype=np.int64)
            mask = np.array([e.attention_mask for e in enc], dtype=np.int64)
            feed = {"input_ids": ids, "attention_mask": mask}
            if "token_type_ids" in self.inputs:
                feed["token_type_ids"] = np.zeros_like(ids)
            hidden = self.sess.run(None, feed)[0]
            m = mask[..., None].astype(np.float32)
            pooled = (hidden * m).sum(1) / np.maximum(m.sum(1), 1e-9)
            norm = np.linalg.norm(pooled, axis=1, keepdims=True)
            out.append(pooled / np.maximum(norm, 1e-9))
        return np.vstack(out).astype(np.float32)

    @staticmethod
    def cosine(a, b):
        """Vectors from encode() are already unit length, so this is a dot
        product. Kept as a named function anyway: an unnormalised vector
        arriving here would silently score wrong, and the name is where that
        assumption is written down."""
        return float(np.dot(a, b))

    @staticmethod
    def rank(query_vec, matrix, top):
        """Indices of the `top` nearest rows, nearest first."""
        scores = matrix @ query_vec
        n = min(top, len(scores))
        idx = np.argpartition(-scores, n - 1)[:n] if n < len(scores) else np.arange(len(scores))
        return idx[np.argsort(-scores[idx])], scores


if __name__ == "__main__":
    import time
    e = Embedder()
    probe = [
        "Daniel's passport expires in March 2027",
        "renewal / passport expiry = 2027-03",
        "The house insurance renews every March",
        "Our dog Nella was born in 2021",
    ]
    t = time.time()
    v = e.encode(probe)
    print("model %s  dims %d  %d texts in %.0f ms"
          % (e.model_id, v.shape[1], len(probe), 1000 * (time.time() - t)))
    q = e.encode(["When does my passport run out?"])[0]
    print("\nquestion: When does my passport run out?")
    for text, vec in zip(probe, v):
        print("  %.3f  %s" % (e.cosine(q, vec), text))
