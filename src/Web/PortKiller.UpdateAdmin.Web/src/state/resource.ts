import { onBeforeUnmount, onMounted, ref, shallowRef } from 'vue';
import { errorText } from '../api/http';
export function useResource<T>(loader: (signal: AbortSignal) => Promise<T>) {
  const data = shallowRef<T>(), error = ref(''), busy = ref(false);
  let controller: AbortController | undefined;
  async function load() {
    controller?.abort(); const own = controller = new AbortController(); busy.value = true; error.value = '';
    try { const result = await loader(own.signal); if (!own.signal.aborted) data.value = result; }
    catch (cause) { if (!own.signal.aborted) { data.value = undefined; error.value = errorText(cause); } }
    finally { if (!own.signal.aborted) busy.value = false; }
  }
  onMounted(load); onBeforeUnmount(() => controller?.abort());
  return { data, error, busy, load };
}
