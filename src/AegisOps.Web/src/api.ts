/** RFC 9457 Problem Details as returned by the API; `reason` is the AI gateway's block reason extension. */
export type ProblemDetails = { title?: string; detail?: string; status?: number; reason?: string; requestId?: string };

/** A non-2xx API response. `message` is the Problem Details title, as before. */
export class ApiError extends Error {
  readonly status: number;
  readonly problem: ProblemDetails;

  constructor(status: number, problem: ProblemDetails, fallback: string) {
    super(problem.title ?? fallback);
    this.name = "ApiError";
    this.status = status;
    this.problem = problem;
  }
}

export async function api<T>(path: string, token: string, init?: RequestInit): Promise<T> {
  const response = await fetch(path, {
    ...init,
    headers: {
      "Content-Type": "application/json",
      Authorization: `Bearer ${token}`,
      ...(init?.headers ?? {}),
    },
  });
  if (!response.ok) {
    const body = (await response.json().catch(() => ({}))) as ProblemDetails;
    throw new ApiError(response.status, body, response.statusText);
  }
  if (response.status === 204) {
    return undefined as T;
  }
  return response.json() as Promise<T>;
}
