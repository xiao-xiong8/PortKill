<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue';
import type { components } from '../api/contracts.generated';
import { api, errorText } from '../api/http';
import { uploadPart } from '../api/upload';
import { openJob, time } from '../api/display';
const props = defineProps<{ id?: string }>();
const manifest = ref<File>(), packageFile = ref<File>(), upload = ref<components['schemas']['UploadSessionResponse']>();
const manifestSent = ref(0), packageSent = ref(0), busy = ref(false), error = ref(''), stage = ref('选择两份签名产物');
let controller: AbortController | undefined;
function pick(event: Event, part: 'manifest' | 'package') { const file = (event.target as HTMLInputElement).files?.[0]; if (part === 'manifest') manifest.value = file; else packageFile.value = file; }
async function readSession(id: string, signal?: AbortSignal) { upload.value = await api(`/api/v1/uploads/${encodeURIComponent(id)}`, 'GET', undefined, signal); manifestSent.value = Number(upload.value?.manifestReceivedBytes); packageSent.value = Number(upload.value?.packageReceivedBytes); }
async function start() {
  if (busy.value) return; busy.value = true; error.value = ''; const own = controller = new AbortController();
  try {
    if (upload.value) await readSession(upload.value.id, own.signal);
    if (upload.value?.state !== 'uploaded') {
      if (!manifest.value || !packageFile.value) throw new Error('Select files');
      if (manifest.value.size < 1 || manifest.value.size > 65536 || packageFile.value.size < 1 || packageFile.value.size > 268435456) { error.value = '清单须为 1–64 KiB，更新包须为 1 byte–256 MiB。'; return; }
      if (!upload.value) { upload.value = await api('/api/v1/uploads/', 'POST', { manifestBytes: manifest.value.size, packageBytes: packageFile.value.size }, own.signal); window.history.replaceState(null, '', '/upload/' + upload.value!.id); window.dispatchEvent(new PopStateEvent('popstate')); }
      if (Number(upload.value!.manifestExpectedBytes) !== manifest.value.size || Number(upload.value!.packageExpectedBytes) !== packageFile.value.size) { error.value = '重新选择的文件大小与原上传不符，请选择原文件或取消后新建上传。'; return; }
      if (Number(upload.value!.manifestReceivedBytes) === 0) { stage.value = '正在传输签名清单'; upload.value = await uploadPart(upload.value!.id, 'manifest', manifest.value, own.signal, bytes => { manifestSent.value = bytes; }); }
      if (Number(upload.value!.packageReceivedBytes) === 0) { stage.value = '正在传输更新包'; upload.value = await uploadPart(upload.value!.id, 'package', packageFile.value, own.signal, bytes => { packageSent.value = bytes; }); }
    }
    stage.value = '提交服务器校验';
    const result = await api<components['schemas']['UploadCompletionResponse']>(`/api/v1/uploads/${upload.value!.id}/complete`, 'POST', {}, own.signal);
    openJob(result.jobId);
  } catch (cause) { error.value = errorText(cause); stage.value = '传输未完成，可整文件重传未确认的文件'; }
  finally { busy.value = false; }
}
async function cancel() {
  if (!upload.value || busy.value) return; error.value = '';
  try { await api(`/api/v1/uploads/${upload.value.id}`, 'DELETE'); upload.value = undefined; stage.value = '本次上传已取消'; manifestSent.value = 0; packageSent.value = 0; window.history.replaceState(null, '', '/upload'); window.dispatchEvent(new PopStateEvent('popstate')); }
  catch (cause) { error.value = errorText(cause); }
}
onMounted(async () => { if (props.id) { busy.value = true; try { await readSession(props.id); stage.value = '已恢复上传记录，请重新选择原文件'; } catch (cause) { error.value = errorText(cause); } finally { busy.value = false; } } });
onBeforeUnmount(() => controller?.abort());
</script>
<template><section class="surface"><div class="section-heading"><h2>上传签名产物</h2><span class="muted">PortKill · stable · win-x64</span></div><div class="split-workspace"><form class="form-stack" @submit.prevent="start"><p v-if="error" class="notice danger" role="alert">{{ error }}</p><label>签名清单（manifest.json）<input type="file" accept=".json" :disabled="busy" @change="pick($event, 'manifest')"></label><div class="file-summary"><span>已传输 {{ manifestSent.toLocaleString() }} / {{ (manifest?.size ?? Number(upload?.manifestExpectedBytes ?? 0)).toLocaleString() }} 字节</span></div><progress v-if="manifest || upload" :value="manifestSent" :max="manifest?.size ?? Number(upload?.manifestExpectedBytes)" aria-label="清单传输进度"></progress><label>四文件更新包（ZIP）<input type="file" accept=".zip" :disabled="busy" @change="pick($event, 'package')"></label><div class="file-summary"><span>已传输 {{ packageSent.toLocaleString() }} / {{ (packageFile?.size ?? Number(upload?.packageExpectedBytes ?? 0)).toLocaleString() }} 字节</span></div><progress v-if="packageFile || upload" :value="packageSent" :max="packageFile?.size ?? Number(upload?.packageExpectedBytes)" aria-label="更新包传输进度"></progress><p role="status" class="muted">{{ stage }}</p><p v-if="upload" class="muted">上传会话 {{ upload.id }} · 到期 {{ time(upload.expiresUtcMs) }}</p><div class="actions"><button class="primary" :disabled="busy || (upload?.state !== 'uploaded' && (!manifest || !packageFile))">{{ upload?.state === 'uploaded' ? '查看校验任务' : '上传并校验' }}</button><button v-if="busy" type="button" @click="controller?.abort()">停止传输</button><button v-if="upload && !busy && upload.state === 'uploading'" type="button" @click="cancel">取消本次上传</button></div></form><aside><h3>从 Windows 制作环境选择文件</h3><p class="muted">上传已签名的清单和对应更新 ZIP。首装包和私钥不用于此流程。</p><h3>中断后重传</h3><p class="muted">可以刷新页面恢复上传记录，重新选择原文件。服务器已经确认的文件会保留，未完成的文件从头传输。</p><p class="muted">上传完成后由服务器验签、验包，再决定是否允许确认发布；传输完成不代表版本已发布。</p><RouterLink to="/history">查看已有任务</RouterLink></aside></div></section></template>
