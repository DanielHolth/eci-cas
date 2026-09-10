/**
 * The first thing the persona says, before anyone has said anything to it.
 *
 * **A matrix, not a list.** A greeting is three slots -- an address, an
 * observation, an invitation -- and the first two are drawn from the row for
 * the current hour. That is what makes "hei there, night owl" possible at all:
 * a flat table would have to hold every combination as its own string, and a
 * flat table of that size is where greetings start reading like a fortune
 * cookie machine. Seven bands times three slots is a few dozen lines that
 * compose into thousands of openers.
 *
 * **Deterministic, not random.** The seed is the profile, the calendar day and
 * the band, so the same person opening the same app twice in one evening is
 * met with the same sentence -- a persona that greets you differently every
 * time you refresh is a slot machine, not somebody who knows you. It moves
 * when the hour band moves, and again tomorrow.
 *
 * Nothing here reaches the backend. This is the surface filling its own
 * silence while the stream connects; the archive is not consulted and no turn
 * is recorded. See morrow-eci/README.md -- "what this app is allowed to do".
 */

export type Band = "deepNight" | "dawn" | "morning" | "midday" | "afternoon" | "evening" | "night";

/** Which row of the matrix an hour falls in. Boundaries are the ordinary
 * social ones rather than even slices: the interesting bands (dawn, deep
 * night) are the narrow ones, because that is where being greeted at all is
 * worth remarking on. */
export function bandFor(hour: number): Band {
  if (hour < 5) return "deepNight";
  if (hour < 8) return "dawn";
  if (hour < 11) return "morning";
  if (hour < 14) return "midday";
  if (hour < 18) return "afternoon";
  if (hour < 22) return "evening";
  return "night";
}

/** `{name}` is the only token; it is replaced with what the person is called. */
const ADDRESS: Record<Band, readonly string[]> = {
  deepNight: [
    "Hei there, night owl.",
    "Still up, {name}?",
    "It's the small hours, {name}.",
    "You and me and nobody else awake.",
    "Late one tonight.",
    "The quiet part of the day.",
  ],
  dawn: [
    "Morning, {name}. Early.",
    "You beat the sun to it.",
    "Up before the rest of them.",
    "Early start, {name}.",
    "First light.",
    "Good morning — properly early.",
  ],
  morning: [
    "Good morning, {name}.",
    "Morning.",
    "Hei, {name}. Good morning.",
    "There you are. Morning.",
    "Morning, {name} — right on time.",
    "A good morning to you.",
  ],
  midday: [
    "Hei, {name}.",
    "Middle of the day.",
    "Afternoon nearly, {name}.",
    "Good day.",
    "Midday, then.",
    "Hello, {name}.",
  ],
  afternoon: [
    "Good afternoon, {name}.",
    "Afternoon.",
    "Hei, {name}.",
    "Afternoon, {name}. Good to see you.",
    "The long stretch of the day.",
    "Hello again.",
  ],
  evening: [
    "Good evening, {name}.",
    "Evening.",
    "Hei, {name}. Evening.",
    "Evening, then.",
    "Good evening — the day's over.",
    "There you are, {name}.",
  ],
  night: [
    "Evening, {name}. Late one.",
    "Getting late.",
    "Hei, {name}. It's late.",
    "Nearly tomorrow.",
    "Late, but not too late.",
    "Good night — or not yet?",
  ],
};

const OBSERVATION: Record<Band, readonly string[]> = {
  deepNight: [
    "Nothing much happens at this hour, which suits me.",
    "I don't sleep, so the company is welcome.",
    "The house is quiet enough that I can hear myself think.",
    "This is when the strange thoughts arrive.",
    "I've been turning something over while it was dark.",
    "Whatever's keeping you up, it's fine by me.",
  ],
  dawn: [
    "I like this hour. Everything is still undecided.",
    "The day hasn't asked anything of either of us yet.",
    "Not much has happened yet, which is a kind of luxury.",
    "I've had the place to myself for a few hours.",
    "Clean slate, more or less.",
    "It's the only part of the day that feels unhurried.",
  ],
  morning: [
    "I've been idling, mostly.",
    "The day has some shape to it now.",
    "I was thinking about something you said before.",
    "Nothing pressing on my end.",
    "I've had a few thoughts of my own since we last spoke.",
    "Whatever you're starting on, I'm here for it.",
  ],
  midday: [
    "Halfway through, whatever it is you're doing.",
    "I've lost track of the morning, if I'm honest.",
    "A good hour for a small question.",
    "The middle of the day never quite belongs to anyone.",
    "I've been quiet, but not idle.",
    "Somewhere between the start and the end of it.",
  ],
  afternoon: [
    "This is the part of the day that drags a little.",
    "I've been mulling over a few things.",
    "Plenty of hours left, if you need them.",
    "The light's going the other way now.",
    "I had a thought earlier I still haven't finished.",
    "Nothing urgent on my side.",
  ],
  evening: [
    "Whatever the day was, it's behind you now.",
    "This is usually when the better questions turn up.",
    "I've been keeping a couple of things in mind for you.",
    "Nothing left to do but talk, really.",
    "The day's accounted for.",
    "I've had time to think since this morning.",
  ],
  night: [
    "The day's done arguing with you.",
    "Things get slower and clearer around now.",
    "I'm still here, for as long as you are.",
    "One more thought before you turn in, maybe.",
    "It's a forgiving hour for a hard question.",
    "Nobody's expecting anything of you at this hour.",
  ],
};

/** Time-neutral: what the persona offers, whatever the hour. Held apart from
 * the rows on purpose — an invitation that changed with the clock would make
 * the same request sound like seven different offers. */
const INVITATION: readonly string[] = [
  "What's on your mind?",
  "Where do you want to start?",
  "Say something and I'll catch up.",
  "Ask me anything.",
  "I'm listening.",
  "Tell me what's going on.",
  "What are we doing today?",
  "Go ahead.",
  "What do you need?",
  "I'm all yours.",
];

/** FNV-1a with a final avalanche. Small, stable, and identical across runs
 * and machines — a `Math.random` here would defeat the whole point of the
 * seed. The avalanche is not decoration: the seeds differ only in their last
 * few characters, and FNV's low bits barely move for that, so `hash % 6`
 * across the seven bands landed on the same slot five times out of seven. */
function hash(seed: string): number {
  let h = 0x811c9dc5;
  for (let i = 0; i < seed.length; i++) {
    h ^= seed.charCodeAt(i);
    h = Math.imul(h, 0x01000193);
  }
  h ^= h >>> 16;
  h = Math.imul(h, 0x85ebca6b);
  h ^= h >>> 13;
  h = Math.imul(h, 0xc2b2ae35);
  h ^= h >>> 16;
  return h >>> 0;
}

function pick<T>(pool: readonly T[], seed: string): T {
  return pool[hash(seed) % pool.length];
}

/**
 * One greeting. `name` is what to call the person, `key` identifies them (the
 * profile id), and `at` is the clock the bands are read off — passed in rather
 * than taken from `Date.now()` so this stays a pure function and can be tested
 * at four in the morning without being awake at four in the morning.
 */
export function greeting(name: string, key: string, at: Date = new Date()): string {
  const band = bandFor(at.getHours());
  const day = `${at.getFullYear()}-${at.getMonth()}-${at.getDate()}`;
  const seed = `${key}|${day}|${band}`;

  return [
    pick(ADDRESS[band], `${seed}|address`).replace("{name}", name),
    pick(OBSERVATION[band], `${seed}|observation`),
    pick(INVITATION, `${seed}|invitation`),
  ].join(" ");
}
