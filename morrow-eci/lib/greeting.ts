/**
 * The first thing the persona says, before anyone has said anything to it.
 *
 * **Terse on purpose.** One line, often one word. An opener that arrives as a
 * paragraph -- a greeting, then a remark about the hour, then an offer to
 * help -- is a receptionist, and it puts words in the persona's mouth before
 * it has heard anything. Morrow says hello and then waits. The hour is the
 * only thing it is allowed to notice unprompted, because that much it knows
 * without being told.
 *
 * **Deterministic, not random.** The seed is the profile, the calendar day and
 * the band, so the same person opening the same app twice in one evening is
 * met with the same word -- a persona that greets you differently every time
 * you refresh is a slot machine, not somebody who knows you. It moves when the
 * hour band moves, and again tomorrow.
 *
 * Nothing here reaches the backend. This is the surface filling its own
 * silence while the stream connects; the archive is not consulted and no turn
 * is recorded. See morrow-eci/README.md -- "what this app is allowed to do".
 */

export type Band = "deepNight" | "dawn" | "morning" | "midday" | "afternoon" | "evening" | "night";

/** Which row an hour falls in. Boundaries are the ordinary social ones rather
 * than even slices: the narrow bands (dawn, deep night) are the ones where
 * being greeted at all is worth remarking on. */
export function bandFor(hour: number): Band {
  if (hour < 5) return "deepNight";
  if (hour < 8) return "dawn";
  if (hour < 11) return "morning";
  if (hour < 14) return "midday";
  if (hour < 18) return "afternoon";
  if (hour < 22) return "evening";
  return "night";
}

/** `{name}` is the only token, and most rows do without it — a name in every
 * greeting reads as a mail merge. */
const GREETING: Record<Band, readonly string[]> = {
  deepNight: ["Night owl.", "Still up.", "Late.", "Hei, {name}.", "Small hours."],
  dawn: ["Early.", "Morning, {name}.", "First light.", "Up already.", "Morning."],
  morning: ["Morning.", "Morning, {name}.", "Hei.", "There you are.", "God morgen."],
  midday: ["Hei.", "Midday.", "Hello, {name}.", "Hei, {name}.", "Halfway."],
  afternoon: ["Afternoon.", "Hei.", "Afternoon, {name}.", "Hello.", "Still here."],
  evening: ["Evening.", "Evening, {name}.", "Hei.", "God kveld.", "Day's done."],
  night: ["Late one.", "Evening, {name}.", "Nearly tomorrow.", "Still up.", "Hei."],
};

/** FNV-1a with a final avalanche. Small, stable, and identical across runs
 * and machines — a `Math.random` here would defeat the whole point of the
 * seed. The avalanche is not decoration: the seeds differ only in their last
 * few characters, and FNV's low bits barely move for that, so the bands kept
 * landing on the same row. */
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

/**
 * One greeting. `name` is what to call the person, `key` identifies them (the
 * profile id), and `at` is the clock the bands are read off — passed in rather
 * than taken from `Date.now()` so this stays a pure function and can be tested
 * at four in the morning without being awake at four in the morning.
 */
export function greeting(name: string, key: string, at: Date = new Date()): string {
  const band = bandFor(at.getHours());
  const day = `${at.getFullYear()}-${at.getMonth()}-${at.getDate()}`;
  const pool = GREETING[band];

  return pool[hash(`${key}|${day}|${band}`) % pool.length].replace("{name}", name);
}
