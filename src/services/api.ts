async function request<T>(path: string, options?: RequestInit): Promise<T> {
  const response = await fetch(`/api/${path}`, options);
  if (!response.ok) {
    const error = await response.json().catch(() => null);
    throw new Error(error?.error ?? `Request failed (${response.status})`);
  }
  if (response.status === 204 || response.headers.get("content-length") === "0") return undefined as T;
  return response.json();
}

export function apiGet<T>(path: string): Promise<T> {
  return request(path);
}

export function apiPost<T>(path: string, body?: unknown): Promise<T> {
  return request(path, {
    method: "POST",
    ...(body === undefined ? {} : {
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    }),
  });
}
