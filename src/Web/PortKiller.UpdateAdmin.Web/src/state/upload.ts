import { computed, ref, shallowRef, watch } from 'vue';
import type { components } from '../api/contracts.generated';
import { api, ApiError, errorText } from '../api/http';
import { uploadPart } from '../api/upload';
import { useAuth } from './auth';

type Upload = components['schemas']['UploadSessionResponse'];
export function createUploadStore(request = api, sendPart = uploadPart) {
  const manifest = shallowRef<File>(), packageFile = shallowRef<File>(), upload = shallowRef<Upload>();
  const manifestSent = ref(0), packageSent = ref(0), busy = ref(false), error = ref('');
  const stage = ref('选择两份签名产物'), completedJob = ref('');
  let controller: AbortController | undefined;
  const current = (own: AbortController) => controller === own && !own.signal.aborted;
  const hasTask = computed(() => busy.value || !!upload.value || !!manifest.value || !!packageFile.value);
  function reset() {
    controller?.abort(); controller = undefined;
    manifest.value = packageFile.value = upload.value = undefined;
    manifestSent.value = packageSent.value = 0; busy.value = false; error.value = completedJob.value = '';
    stage.value = '选择两份签名产物';
  }
  function pick(file: File | undefined, part: 'manifest' | 'package') {
    if (busy.value || completedJob.value) return;
    if (part === 'manifest') manifest.value = file;
    else packageFile.value = file;
  }
  function accept(value: Upload, own: AbortController) {
    if (!current(own)) return;
    upload.value = value;
    manifestSent.value = Number(value.manifestReceivedBytes);
    packageSent.value = Number(value.packageReceivedBytes);
  }
  async function restore(id: string) {
    if (upload.value?.id === id || hasTask.value) return;
    const own = controller = new AbortController(); busy.value = true; error.value = '';
    stage.value = '正在读取上传记录…';
    try {
      accept(await request<Upload>(`/api/v1/uploads/${encodeURIComponent(id)}`, 'GET', undefined, own.signal), own);
      if (current(own)) stage.value = upload.value?.state === 'cancelled' || upload.value?.state === 'expired'
        ? '上传会话已关闭，请新建上传' : '已恢复上传记录，请重新选择原文件';
    } catch (cause) { if (current(own)) error.value = errorText(cause); }
    finally { if (controller === own) { busy.value = false; controller = undefined; } }
  }
  async function start() {
    if (busy.value || completedJob.value) return;
    const own = controller = new AbortController(); busy.value = true; error.value = '';
    try {
      if (upload.value) accept(await request<Upload>(`/api/v1/uploads/${upload.value.id}`, 'GET', undefined, own.signal), own);
      if (!current(own)) return;
      if (upload.value?.state !== 'uploaded') {
        const selectedManifest = manifest.value, selectedPackage = packageFile.value;
        const needManifest = !upload.value || Number(upload.value.manifestReceivedBytes) === 0;
        const needPackage = !upload.value || Number(upload.value.packageReceivedBytes) === 0;
        if (needManifest && !selectedManifest || needPackage && !selectedPackage) {
          error.value = '请选择尚未确认的原文件。'; return;
        }
        if (selectedManifest && (selectedManifest.size < 1 || selectedManifest.size > 65536)
          || selectedPackage && (selectedPackage.size < 1 || selectedPackage.size > 268435456)) {
          error.value = '清单须为 1–64 KiB，更新包须为 1 byte–256 MiB。'; return;
        }
        if (!upload.value) {
          accept(await request<Upload>('/api/v1/uploads/', 'POST', {
            manifestBytes: selectedManifest!.size, packageBytes: selectedPackage!.size,
          }, own.signal), own);
        }
        if (!current(own)) return;
        if (selectedManifest && Number(upload.value!.manifestExpectedBytes) !== selectedManifest.size
          || selectedPackage && Number(upload.value!.packageExpectedBytes) !== selectedPackage.size) {
          error.value = '重新选择的文件大小与原上传不符，请选择原文件或取消后新建上传。'; return;
        }
        if (Number(upload.value!.manifestReceivedBytes) === 0) {
          stage.value = '正在传输签名清单';
          accept(await sendPart(upload.value!.id, 'manifest', selectedManifest!, own.signal,
            bytes => { if (current(own)) manifestSent.value = bytes; }), own);
        }
        if (!current(own)) return;
        if (Number(upload.value!.packageReceivedBytes) === 0) {
          stage.value = '正在传输更新包';
          accept(await sendPart(upload.value!.id, 'package', selectedPackage!, own.signal,
            bytes => { if (current(own)) packageSent.value = bytes; }), own);
        }
      }
      if (!current(own)) return;
      stage.value = '提交服务器校验';
      const result = await request<components['schemas']['UploadCompletionResponse']>(
        `/api/v1/uploads/${upload.value!.id}/complete`, 'POST', {}, own.signal);
      if (!current(own)) return;
      completedJob.value = result.jobId; stage.value = '上传完成，已提交校验';
      manifest.value = packageFile.value = undefined;
    } catch (cause) {
      if (controller !== own) return;
      error.value = own.signal.aborted ? '' : errorText(cause);
      if (cause instanceof ApiError && cause.code === 'upload_closed' && upload.value)
        upload.value = { ...upload.value, state: 'expired' };
      stage.value = '传输未完成，可整文件重传未确认的文件';
      manifestSent.value = Number(upload.value?.manifestReceivedBytes ?? 0);
      packageSent.value = Number(upload.value?.packageReceivedBytes ?? 0);
    } finally { if (controller === own) { busy.value = false; controller = undefined; } }
  }
  function stop() { controller?.abort(); }
  async function cancel() {
    if (!upload.value || busy.value) return;
    const own = controller = new AbortController(); busy.value = true; error.value = '';
    try {
      await request(`/api/v1/uploads/${upload.value.id}`, 'DELETE', undefined, own.signal);
      if (current(own)) { reset(); stage.value = '本次上传已取消'; }
    } catch (cause) { if (current(own)) error.value = errorText(cause); }
    finally { if (controller === own) { busy.value = false; controller = undefined; } }
  }
  const canStart = computed(() => !busy.value && !completedJob.value && (upload.value?.state === 'uploaded'
    || (!upload.value || upload.value.state === 'uploading')
    && (!!manifest.value || Number(upload.value?.manifestReceivedBytes) > 0)
    && (!!packageFile.value || Number(upload.value?.packageReceivedBytes) > 0)));
  return { manifest, packageFile, upload, manifestSent, packageSent, busy, error, stage, completedJob,
    hasTask, canStart, pick, restore, start, stop, cancel, reset };
}

const store = createUploadStore(), auth = useAuth();
watch(() => {
  const session = auth.session.value;
  return session?.phase === 'full' && auth.can('release.upload')
    ? JSON.stringify([session.userId, session.currentSessionId]) : '';
}, () => store.reset(), { flush: 'sync' });
export const useUpload = () => store;
