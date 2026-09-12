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

  useEffect(() => {
    // localStorage is not there during the server render, so the first paint
    // has to happen before the answer is known.
    // eslint-disable-next-line react-hooks/set-state-in-effect
    setAccount(readAccount());
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

  return <Conversation account={account} onEdit={() => setEditing(true)} />;
}
