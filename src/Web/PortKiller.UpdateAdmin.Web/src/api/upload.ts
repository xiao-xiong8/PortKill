import type { components } from './contracts.generated';
import { ApiError, csrfToken, readResponse, reportAuthentication } from './http';
export async function uploadPart(id: string, part: 'manifest' | 'package', file: File, signal: AbortSignal, progress: (bytes: number) => void) {
  const token = await csrfToken(signal), path = `/api/v1/uploads/${encodeURIComponent(id)}/${part}`;
  if (signal.aborted) throw new DOMException('Cancelled', 'AbortError');
  return new Promise<components['schemas']['UploadSessionResponse']>((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    const abort = () => xhr.abort();
    const fail = (error: unknown) => { signal.removeEventListener('abort', abort); reportAuthentication(error, path); reject(error); };
    xhr.open('PUT', path); xhr.timeout = 600000;
    xhr.setRequestHeader('Content-Type', 'application/octet-stream'); xhr.setRequestHeader('X-PortKill-CSRF', token);
    xhr.upload.onprogress = event => progress(Math.min(file.size, event.loaded));
    xhr.onerror = () => fail(new ApiError(0, 'network_error', '上传连接中断。'));
    xhr.ontimeout = () => fail(new ApiError(0, 'upload_timeout', '上传超时，请重新传输未完成的文件。'));
    xhr.onabort = () => fail(new DOMException('Cancelled', 'AbortError'));
    xhr.onload = () => {
      signal.removeEventListener('abort', abort);
      if (new URL(xhr.responseURL).origin !== window.location.origin) { fail(new ApiError(0, 'invalid_response', '上传返回了意外地址。')); return; }
      void readResponse<components['schemas']['UploadSessionResponse']>(new Response(xhr.responseText, { status: xhr.status, headers: {
        'content-type': xhr.getResponseHeader('content-type') ?? '', 'retry-after': xhr.getResponseHeader('retry-after') ?? '',
      } })).then(result => { progress(file.size); resolve(result); }, fail);
    };
    signal.addEventListener('abort', abort, { once: true }); xhr.send(file);
  });
}
