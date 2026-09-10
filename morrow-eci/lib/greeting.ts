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
 * **Random, without repeats.** A line is drawn fresh on every open from the
 * hour band's pool, skipping whichever one this person heard last.
 *
 * Nothing here reaches the backend. This is the surface filling its own
 * silence while the stream connects; the archive is not consulted and no turn
 * is recorded. See morrow-eci/README.md -- "what this app is allowed to do".
 */

/** `egg` is reported rather than kept private: the surface follows a rare
 * opening with a nudge, so the persona says something of its own after it.
 * See morrow-eci/components/Conversation.tsx. */
export interface Greeting {
  text: string;
  egg: boolean;
}

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
  deepNight: ["Night owl.", "Still up.", "Late.", "Hello, {name}.", "Small hours."],
  dawn: ["Early.", "Morning, {name}.", "First light.", "Up already.", "Morning."],
  morning: ["Morning.", "Morning, {name}.", "Hello.", "There you are.", "Good morning."],
  midday: ["Hello.", "Midday.", "Hello, {name}.", "Still going.", "Halfway."],
  afternoon: ["Afternoon.", "Hello.", "Afternoon, {name}.", "Good afternoon.", "Still here."],
  evening: ["Evening.", "Evening, {name}.", "Hello.", "Good evening.", "Day's done."],
  night: ["Late one.", "Evening, {name}.", "Nearly tomorrow.", "Still up.", "Hello."],
};

/**
 * One line per band that displaces the ordinary greeting about once in forty
 * — see ODDS. Rare enough to be noticed rather than expected, which is the
 * whole trick: a surprise on a fixed rota is a feature, and a feature this
 * small should not be one.
 *
 * Short, and asking for nothing. Not every band has one — a household that
 * produced a quip at every hour would be trying too hard.
 */
const EGG: Partial<Record<Band, string>> = {
  deepNight: "Just us, then.",
  dawn: "Birds first.",
  morning: "Coffee?",
  midday: "Lunch?",
  night: "Get some sleep.",
};

/** Roughly how many greetings pass between eggs. */
const ODDS = 40;

const LAST_KEY = "morrow.lastGreeting.";

/**
 * One greeting. `name` is what to call the person, `key` identifies them (the
 * profile id), and `at` is the clock the bands are read off. `roll` is the
 * randomness, injectable so a test can pin it.
 *
 * Random per open, but never the same line twice in a row for one person: a
 * deterministic day-and-band seed meant the same word all evening, which read
 * as broken rather than as familiar.
 */
export function greeting(name: string, key: string, at: Date = new Date(), roll: () => number = Math.random): Greeting {
  const band = bandFor(at.getHours());

  const egg = EGG[band];
  if (egg && roll() * ODDS < 1) {
    return { text: egg, egg: true };
  }

  let last: string | null = null;
  try { last = window.localStorage.getItem(LAST_KEY + key); } catch { /* no storage: plain random */ }

  const pool = GREETING[band].filter((g) => g !== last);
  const pick = pool[Math.floor(roll() * pool.length)];
  try { window.localStorage.setItem(LAST_KEY + key, pick); } catch { /* holds for this open only */ }
  return { text: pick.replace("{name}", name), egg: false };
}
