"use client";

import { useCallback, useEffect, useMemo, useState } from "react";
import { Conversation } from "@/components/Conversation";
import { ProfilePicker } from "@/components/ProfilePicker";
import {
  createProfile,
  fetchProfiles,
  readActiveProfileId,
  writeActiveProfileId,
  type Profile,
} from "@/lib/profiles";

/**
 * Owns who is talking; Conversation owns the talking itself. The picker is
 * the cold-start screen — with no profile chosen there is nobody to attribute
 * a turn to, and attributing it to nobody would colour the persona's
 * device-wide mood on everyone's behalf.
 */
export default function Home() {
  const [profiles, setProfiles] = useState<Profile[]>([]);
  const [activeId, setActiveId] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [switching, setSwitching] = useState(false);

  const active = useMemo(
    () => profiles.find((profile) => profile.id === activeId) ?? null,
    [profiles, activeId],
  );

  const load = useCallback(async () => {
    try {
      const found = await fetchProfiles();
      setProfiles(found);
      setError(null);

      // A remembered id whose profile is gone from disk falls back to the
      // picker rather than silently talking as nobody.
      const remembered = readActiveProfileId();
      setActiveId(found.some((profile) => profile.id === remembered) ? remembered : null);
    } catch {
      setError("Can't reach Morrow. Is the host running?");
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    // Subscribing to an external system (the host's profile registry) on
    // mount. Every setState in `load` happens after an await, so there is no
    // synchronous cascade — the rule can't see past the call.
    // eslint-disable-next-line react-hooks/set-state-in-effect
    void load();
  }, [load]);

  function selectProfile(profile: Profile) {
    writeActiveProfileId(profile.id);
    setActiveId(profile.id);
    setSwitching(false);
  }

  async function handleCreate(displayName: string, avatar: string) {
    try {
      const profile = await createProfile(displayName, avatar);
      setProfiles((current) =>
        current.some((existing) => existing.id === profile.id) ? current : [...current, profile],
      );
      setError(null);
      selectProfile(profile);
    } catch {
      setError("Couldn't create that profile.");
    }
  }

  if (!active || switching) {
    return (
      <ProfilePicker
        profiles={profiles}
        loading={loading}
        error={error}
        onSelect={selectProfile}
        onCreate={handleCreate}
        onCancel={active ? () => setSwitching(false) : undefined}
      />
    );
  }

  return <Conversation key={active.id} profile={active} onSwitch={() => setSwitching(true)} />;
}
