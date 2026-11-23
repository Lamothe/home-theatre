const migrationKey = "atomic-video-history-imported";

export async function importAtomicHistory(): Promise<void> {
  if (localStorage.getItem(migrationKey)) return;
  const response = await fetch("/api/history/atomic");
  if (response.status === 204) return;
  if (!response.ok) throw new Error(`History request failed (${response.status})`);

  const history: unknown = await response.json();
  if (!history || typeof history !== "object" || Array.isArray(history)) {
    throw new Error("Expected an object containing Atomic video history.");
  }

  for (const [key, value] of Object.entries(history)) {
    const watched = /^video-watched:[A-Za-z0-9_/-]+$/.test(key) && value === "1";
    const progress = /^video-progress:[A-Za-z0-9_/-]+$/.test(key)
      && typeof value === "string" && /^\d+$/.test(value) && Number.isSafeInteger(Number(value));
    if ((watched || progress) && localStorage.getItem(key) === null) {
      localStorage.setItem(key, value);
    }
  }
  localStorage.setItem(migrationKey, "1");
}
