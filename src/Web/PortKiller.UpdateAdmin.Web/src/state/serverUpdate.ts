import { ref, shallowRef } from 'vue';
import { api, errorText } from '../api/http';
import { uploadServerPackage, type ServerLog, type ServerSummary, type ServerTask } from '../api/serverUpdate';

// This store is owned by the authenticated application, not by a route component.
export function createServerUpdateStore(request = api, send = uploadServerPackage) {
  const summary = shallowRef<ServerSummary>(), task = shallowRef<ServerTask>(), logs = ref<ServerLog[]>([]);
  const busy = ref(false), sent = ref(0), total = ref(0), error = ref(''), connection = ref('');
  let uploading: AbortController | undefined, generation = 0, refreshing = false, selection = 0;
  function select(next: ServerTask | undefined) {
    if (next?.id !== task.value?.id) { selection++; logs.value = []; }
    task.value = next;
  }
  function reset() { generation++; uploading?.abort(); uploading = undefined; summary.value = undefined; task.value = undefined; logs.value = []; busy.value = false; error.value = ''; connection.value = ''; }
  async function refresh() {
    if (refreshing) return;
    refreshing = true; const own = generation;
    const controller = new AbortController(), timeout = setTimeout(() => controller.abort(), 8000);
    try {
      const result = await request<ServerSummary>('/api/v1/server-updates/', 'GET', undefined, controller.signal);
      if (own !== generation) return;
      summary.value = result;
      const ordered = [...result.tasks].sort((a, b) => b.updatedUtcMs - a.updatedUtcMs);
      const selected = ordered.find(t => !t.terminal || t.state === 'recoveryRequired') ?? ordered[0];
      // An action may return a task before the summary endpoint observes it.
      if (!task.value || selected?.id === task.value.id || (selected && selected.updatedUtcMs >= task.value.updatedUtcMs)) select(selected);
      const current = task.value, ownSelection = selection;
      if (current) {
        const entries = await request<ServerLog[]>(`/api/v1/server-updates/tasks/${current.id}/logs?after=${logs.value.at(-1)?.seq ?? 0}`, 'GET', undefined, controller.signal);
        if (own !== generation || ownSelection !== selection) return;
        const cursor = logs.value.at(-1)?.seq ?? 0;
        logs.value = [...logs.value, ...entries.filter(item => item.seq > cursor)].slice(-1000);
      }
      connection.value = '';
    } catch (cause) {
      if (own === generation) connection.value = (task.value && !task.value.terminal
        ? '连接中断，正在重连；已接受的更新任务继续执行，结果将在连接恢复后显示。'
        : '状态暂时无法读取，正在重连；已保留最近结果。') + ' ' + errorText(cause);
    } finally { clearTimeout(timeout); refreshing = false; }
  }
  async function upload(file: File) {
    if (busy.value) return;
    if (!file.name.toLowerCase().endsWith('.zip') || !file.size || file.size > Number(summary.value?.maximumPackageBytes ?? 0)) { error.value = '请选择符合服务器大小限制的 ZIP 文件。'; return; }
    const own = generation, controller = uploading = new AbortController();
    busy.value = true; error.value = ''; sent.value = 0; total.value = file.size;
    try { await send(file, controller.signal, bytes => { if (own === generation) sent.value = bytes; }); if (own === generation) await refresh(); }
    catch (cause) { if (own === generation) error.value = errorText(cause); }
    finally { if (own === generation) { busy.value = false; uploading = undefined; } }
  }
  async function action(path: string, method = 'POST') {
    if (busy.value) return;
    const own = generation; busy.value = true; error.value = '';
    try {
      const result = await request<ServerTask | undefined>(path, method, method === 'POST' ? {} : undefined);
      if (own === generation) { if (method === 'POST' && result?.id) select(result); await refresh(); }
    }
    catch (cause) { if (own === generation) { error.value = errorText(cause); await refresh(); } }
    finally { if (own === generation) busy.value = false; }
  }
  return { summary, task, logs, busy, sent, total, error, connection, upload, refresh, action, reset };
}
const store = createServerUpdateStore();
export const useServerUpdate = () => store;
