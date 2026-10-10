"use client";

import { useEffect } from "react";
import { tr } from "./i18n/tr";

/** Warns before leaving the page (reload/close/external navigation) while there are unsaved edits. */
export function useUnsavedChangesWarning(dirty: boolean) {
  useEffect(() => {
    if (!dirty) return;
    const handler = (e: BeforeUnloadEvent) => {
      e.preventDefault();
      e.returnValue = tr.common.unsavedChanges;
      return tr.common.unsavedChanges;
    };
    window.addEventListener("beforeunload", handler);
    return () => window.removeEventListener("beforeunload", handler);
  }, [dirty]);
}
