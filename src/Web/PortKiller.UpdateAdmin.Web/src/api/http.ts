export class ApiError extends Error {
  constructor(public status: number, public code: string, message: string, public retryAfter = 0) { super(message); }
}

// Preserve int64 values beyond JavaScript's safe integer range when the browser supports source-aware JSON.
export function parseJson<T>(text: string): T {
  return JSON.parse(text, ((_: string, value: unknown, context?: { source?: string }) => {
    if (typeof value === 'number' && Number.isInteger(value) && !Number.isSafeInteger(value)) {
      if (context?.source && /^-?\d+$/.test(context.source)) return context.source;
      throw new ApiError(0, 'unsupported_precision', '当前浏览器无法准确读取版本序号，请更新浏览器。');
    }
    return value;
  }) as Parameters<typeof JSON.parse>[1]) as T;
}

export async function readResponse<T>(response: Response): Promise<T> {
  if (response.status === 204) return undefined as T;
  const json = response.headers.get('content-type')?.includes('json');
  const data = json ? parseJson<Record<string, unknown>>(await response.text()) : {};
  if (!response.ok) {
    const code = typeof data.code === 'string' ? data.code : 'request_failed';
    const detail = typeof data.detail === 'string' ? data.detail : `请求失败（${response.status}）`;
    throw new ApiError(response.status, code, detail, Math.min(600, Math.max(0, Number(response.headers.get('retry-after')) || 0)));
  }
  if (!json) throw new ApiError(0, 'invalid_response', '服务返回了无法识别的内容，请检查连接。');
  return data as T;
}

export async function csrfToken(signal?: AbortSignal): Promise<string> {
  const data = await readResponse<{ requestToken: string }>(await fetch('/api/v1/auth/csrf', {
    credentials: 'same-origin', redirect: 'error', cache: 'no-store', signal,
  }));
  if (typeof data.requestToken !== 'string') throw new ApiError(0, 'invalid_response', '请求验证不可用。');
  return data.requestToken;
}

export function reportAuthentication(error: unknown, path: string) {
  if (error instanceof ApiError && error.status === 401 && !/^\/api\/v1\/auth\/(login|mfa\/|reauthenticate)/.test(path))
    window.dispatchEvent(new Event('admin:unauthorized'));
}

export async function api<T>(path: string, method = 'GET', body?: unknown, signal?: AbortSignal,
  extraHeaders?: HeadersInit): Promise<T> {
  if (!path.startsWith('/api/v1/')) throw new Error('Invalid management path.');
  try {
    const headers = new Headers();
    if (method !== 'GET') headers.set('X-PortKill-CSRF', await csrfToken(signal));
    if (body !== undefined) headers.set('Content-Type', 'application/json');
    if (extraHeaders) for (const [name, value] of new Headers(extraHeaders)) headers.set(name, value);
    return await readResponse<T>(await fetch(path, { method, headers, credentials: 'same-origin', redirect: 'error',
      cache: 'no-store', body: body === undefined ? undefined : JSON.stringify(body), signal }));
  } catch (error) { reportAuthentication(error, path); throw error; }
}

export function errorText(error: unknown): string {
  if (error instanceof ApiError) return error.message + (error.retryAfter ? ` 请在 ${error.retryAfter} 秒后重试。` : '')
    + (error.status === 403 ? ' 请核对账号权限，必要时完成再次认证。' : '');
  return error instanceof DOMException && error.name === 'AbortError' ? '操作已取消。' : '连接中断，请检查网络后重试。';
}
