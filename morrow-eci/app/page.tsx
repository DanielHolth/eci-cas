"use client";

import { useEffect, useState } from "react";
import { Conversation } from "@/components/Conversation";
import { AccountSetup } from "@/components/AccountSetup";
import { readAccount, writeAccount, type Account } from "@/lib/account";

/**
 * Owns who is talking; Conversation owns the talking itself. Setup is the
 * cold-start screen — until there is a name there is nothing to call the
 * person in a greeting.
 */
export default function Home() {
  const [account, setAccount] = useState<Account | null>(null);
  const [ready, setReady] = useState(false);
  const [editing, setEditing] = useState(false);

  // Whether something else is already doing the talking. The desktop shell
  // opens this window as ?mute=1 because the overlay owns the voice; a plain
  // browser tab has no query string and speaks. Read here rather than in
  // Conversation so there is one place that knows the URL is a surface
  // decision -- see lib/useSpeech.ts.
  const [mute, setMute] = useState(false);

  useEffect(() => {
    // localStorage is not there during the server render, so the first paint
    // has to happen before the answer is known. Same for the query string:
    // the export is prerendered once, at build time, without one.
    // eslint-disable-next-line react-hooks/set-state-in-effect
    setAccount(readAccount());
    setMute(new URLSearchParams(window.location.search).has("mute"));
    setReady(true);
  }, []);

  function save(next: Account) {
    writeAccount(next);
    setAccount(next);
    setEditing(false);
  }

  if (!ready) {
    return null;
  }

  if (!account || editing) {
    return (
      <AccountSetup
        account={account}
        onSave={save}
        onCancel={account ? () => setEditing(false) : undefined}
      />
    );
  }

  return <Conversation account={account} onEdit={() => setEditing(true)} mute={mute} />;
}
