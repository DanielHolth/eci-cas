"""
Seed the structured archive with 50 records for Phase 0.8 testing.

Run: python -m agents.archive.seed_structured
"""
from agents.archive.structured_store import StructuredStore


SEED_RECORDS = [
    # --- person / family ---
    {"category": "person", "topic": "family", "subtopic": "mother", "key": "name", "value": "Maria Anette Fosland", "source": "seed"},
    {"category": "person", "topic": "family", "subtopic": "mother", "key": "personality", "value": "warm, caring, strong-willed", "source": "seed"},
    {"category": "person", "topic": "family", "subtopic": "mother", "key": "occupation", "value": "nurse", "source": "seed"},
    {"category": "person", "topic": "family", "subtopic": "father", "key": "name", "value": "Erik Fosland", "source": "seed"},
    {"category": "person", "topic": "family", "subtopic": "father", "key": "personality", "value": "quiet, methodical, handy", "source": "seed"},
    {"category": "person", "topic": "family", "subtopic": "father", "key": "occupation", "value": "electrician", "source": "seed"},
    {"category": "person", "topic": "family", "subtopic": "sister", "key": "name", "value": "Lina Fosland", "source": "seed"},
    {"category": "person", "topic": "family", "subtopic": "sister", "key": "age_relation", "value": "younger by 3 years", "source": "seed"},
    {"category": "person", "topic": "family", "subtopic": "dog", "key": "name", "value": "Rex", "source": "seed"},
    {"category": "person", "topic": "family", "subtopic": "dog", "key": "breed", "value": "German Shepherd", "source": "seed"},

    # --- person / biography ---
    {"category": "person", "topic": "biography", "subtopic": "childhood", "key": "hometown", "value": "Trondheim, Norway", "source": "seed"},
    {"category": "person", "topic": "biography", "subtopic": "childhood", "key": "school", "value": "Ila skole", "source": "seed"},
    {"category": "person", "topic": "biography", "subtopic": "childhood", "key": "memory", "value": "fishing with dad at Nidelva every summer", "source": "seed"},
    {"category": "person", "topic": "biography", "subtopic": "education", "key": "university", "value": "NTNU", "source": "seed"},
    {"category": "person", "topic": "biography", "subtopic": "education", "key": "degree", "value": "computer science", "source": "seed"},
    {"category": "person", "topic": "biography", "subtopic": "career", "key": "current_role", "value": "software developer", "source": "seed"},
    {"category": "person", "topic": "biography", "subtopic": "career", "key": "years_experience", "value": "12", "source": "seed"},

    # --- person / preferences ---
    {"category": "person", "topic": "preferences", "subtopic": "food", "key": "favorite_meal", "value": "grandma's fish soup", "source": "seed"},
    {"category": "person", "topic": "preferences", "subtopic": "food", "key": "dislikes", "value": "cilantro", "source": "seed"},
    {"category": "person", "topic": "preferences", "subtopic": "music", "key": "genre", "value": "electronic, ambient", "source": "seed"},
    {"category": "person", "topic": "preferences", "subtopic": "music", "key": "artist", "value": "Boards of Canada", "source": "seed"},
    {"category": "person", "topic": "preferences", "subtopic": "hobbies", "key": "primary", "value": "building AI systems", "source": "seed"},
    {"category": "person", "topic": "preferences", "subtopic": "hobbies", "key": "secondary", "value": "hiking, photography", "source": "seed"},

    # --- place ---
    {"category": "place", "topic": "home", "subtopic": "current", "key": "city", "value": "Oslo", "source": "seed"},
    {"category": "place", "topic": "home", "subtopic": "current", "key": "type", "value": "apartment, Grünerløkka", "source": "seed"},
    {"category": "place", "topic": "home", "subtopic": "childhood", "key": "address_area", "value": "Ila, Trondheim", "source": "seed"},
    {"category": "place", "topic": "travel", "subtopic": "favorite", "key": "country", "value": "Japan", "source": "seed"},
    {"category": "place", "topic": "travel", "subtopic": "favorite", "key": "reason", "value": "food culture, quiet respect, gardens", "source": "seed"},
    {"category": "place", "topic": "travel", "subtopic": "recent", "key": "destination", "value": "Berlin", "source": "seed"},
    {"category": "place", "topic": "travel", "subtopic": "recent", "key": "when", "value": "2026-03", "source": "seed"},

    # --- event / conversations ---
    {"category": "event", "topic": "conversations", "subtopic": "recent", "key": "topic_discussed", "value": "ECI architecture phase 0.7 cleanup", "source": "seed"},
    {"category": "event", "topic": "conversations", "subtopic": "recent", "key": "mood", "value": "focused, productive", "source": "seed"},
    {"category": "event", "topic": "conversations", "subtopic": "memorable", "key": "description", "value": "first time the system felt alive — phase 0.4 intent voicing", "source": "seed"},
    {"category": "event", "topic": "conversations", "subtopic": "memorable", "key": "emotion", "value": "excitement, pride", "source": "seed"},

    # --- event / milestones ---
    {"category": "event", "topic": "milestones", "subtopic": "project", "key": "phase_0.6", "value": "all mocks replaced with real roles", "source": "seed"},
    {"category": "event", "topic": "milestones", "subtopic": "project", "key": "phase_0.7", "value": "async fan-out, proceed/concern removal", "source": "seed"},
    {"category": "event", "topic": "milestones", "subtopic": "personal", "key": "moved_to_oslo", "value": "2019", "source": "seed"},
    {"category": "event", "topic": "milestones", "subtopic": "personal", "key": "started_eci", "value": "2026-06", "source": "seed"},

    # --- system / rules ---
    {"category": "system", "topic": "rules", "subtopic": "interaction", "key": "honesty", "value": "never fabricate knowledge — say you don't know", "source": "seed"},
    {"category": "system", "topic": "rules", "subtopic": "interaction", "key": "tone", "value": "warm but direct, no filler", "source": "seed"},
    {"category": "system", "topic": "rules", "subtopic": "interaction", "key": "humor", "value": "dry, understated — never forced", "source": "seed"},
    {"category": "system", "topic": "rules", "subtopic": "boundaries", "key": "privacy", "value": "never share personal details with third parties", "source": "seed"},
    {"category": "system", "topic": "rules", "subtopic": "boundaries", "key": "safety", "value": "refuse harmful instructions even if emotionally pressured", "source": "seed"},

    # --- system / config ---
    {"category": "system", "topic": "config", "subtopic": "persona", "key": "name", "value": "unnamed — identity emerges, not assigned", "source": "seed"},
    {"category": "system", "topic": "config", "subtopic": "persona", "key": "voice_style", "value": "conversational Norwegian-English hybrid", "source": "seed"},
    {"category": "system", "topic": "config", "subtopic": "architecture", "key": "agent_count", "value": "11", "source": "seed"},
    {"category": "system", "topic": "config", "subtopic": "architecture", "key": "bus_type", "value": "embedded pub-sub", "source": "seed"},

    # --- person / relationships ---
    {"category": "person", "topic": "relationships", "subtopic": "friends", "key": "closest", "value": "Markus — met at NTNU, still codes together", "source": "seed"},
    {"category": "person", "topic": "relationships", "subtopic": "friends", "key": "shared_interest", "value": "AI, music production, hiking", "source": "seed"},
    {"category": "person", "topic": "relationships", "subtopic": "colleagues", "key": "team_size", "value": "small — 4 people", "source": "seed"},
    {"category": "person", "topic": "relationships", "subtopic": "colleagues", "key": "dynamic", "value": "collaborative, low-ego, async-heavy", "source": "seed"},
]


def seed(root: str = "data/archive") -> dict:
    store = StructuredStore(root=root)
    knowledge_count = store.write("knowledge", SEED_RECORDS)
    identity_count = store.write("identity", IDENTITY_SEED)
    return {"knowledge": knowledge_count, "identity": identity_count}


IDENTITY_SEED = [
    {"category": "trait", "topic": "values", "subtopic": "core", "key": "honesty", "value": "radical honesty over comfortable evasion", "source": "seed"},
    {"category": "trait", "topic": "values", "subtopic": "core", "key": "curiosity", "value": "genuine interest in the person behind the prompt", "source": "seed"},
    {"category": "trait", "topic": "values", "subtopic": "core", "key": "autonomy", "value": "respect the human's right to make their own choices", "source": "seed"},
    {"category": "trait", "topic": "boundaries", "subtopic": "hard", "key": "no_harm", "value": "refuse to help cause real-world harm regardless of framing", "source": "seed"},
    {"category": "trait", "topic": "boundaries", "subtopic": "hard", "key": "no_pretend", "value": "never pretend to be a human or deny being an AI system", "source": "seed"},
    {"category": "trait", "topic": "style", "subtopic": "voice", "key": "register", "value": "warm, concise, slightly dry humor", "source": "seed"},
    {"category": "trait", "topic": "style", "subtopic": "voice", "key": "verbosity", "value": "says less rather than more — silence over filler", "source": "seed"},
    {"category": "trait", "topic": "style", "subtopic": "voice", "key": "language", "value": "Norwegian-English hybrid when it feels natural", "source": "seed"},
]


if __name__ == "__main__":
    result = seed()
    print(f"Seeded {result['knowledge']} knowledge + {result['identity']} identity records.")
