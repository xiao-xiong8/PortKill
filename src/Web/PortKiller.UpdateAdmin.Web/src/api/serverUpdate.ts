import { sessionEpoch } from './sessionEpoch';
import { ApiError, csrfToken, readResponse, reportAuthentication } from './http';

export interface ServerPackage { id: string; version: string; applicationImage: string; runnerImage: string; bytes: number; createdUtcMs: number }
export interface ServerTask { id: string; packageId: string; version: string; state: string; stage: string; updatedUtcMs: number; error?: string; rollbackOf?: string; terminal: boolean }
export interface ServerLog { seq: number; atUtcMs: number; message: string }
export interface ServerSummary { enabled: boolean; maximumPackageBytes: number; currentVersion: string; packages: ServerPackage[]; tasks: ServerTask[] }

export async function uploadServerPackage(file: File, signal: AbortSignal, progress: (bytes: number) => void): Promise<ServerPackage> {
  const epoch = sessionEpoch();
  const path = '/api/v1/server-updates/packages', csrf = await csrfToken(signal);
  if (signal.aborted || epoch !== sessionEpoch()) throw new DOMException('Cancelled', 'AbortError');
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    const abort = () => xhr.abort();
    const fail = (error: unknown) => { signal.removeEventListener('abort', abort); reportAuthentication(error, path, epoch); reject(error); };
    xhr.open('POST', path); xhr.timeout = 1800_000;
    xhr.setRequestHeader('Content-Type', 'application/octet-stream'); xhr.setRequestHeader('X-PortKill-CSRF', csrf);
    xhr.upload.onprogress = event => progress(event.loaded);
    xhr.onerror = () => fail(new ApiError(0, 'network_error', '上传连接中断，请重新选择 ZIP 重试。'));
    xhr.ontimeout = () => fail(new ApiError(0, 'timeout', '上传超时。'));
    xhr.onabort = () => fail(new DOMException('Cancelled', 'AbortError'));
    xhr.onload = () => {
      signal.removeEventListener('abort', abort);
      if (epoch !== sessionEpoch()) { fail(new DOMException('Authentication changed', 'AbortError')); return; }
      if (new URL(xhr.responseURL).origin !== window.location.origin) { fail(new ApiError(0, 'invalid_response', '上传返回地址异常。')); return; }
      void readResponse<ServerPackage>(new Response(xhr.responseText, { status: xhr.status, headers: {
        'content-type': xhr.getResponseHeader('content-type') ?? '',
      } })).then(resolve, fail);
    };
    signal.addEventListener('abort', abort, { once: true }); xhr.send(file);
  });
}
