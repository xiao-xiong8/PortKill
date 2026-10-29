<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, onUnmounted, ref } from 'vue';
import { api, errorText } from '../api/http';
import { finishStrong, useAuth } from '../state/auth';
const auth = useAuth(), dialog = ref<HTMLDialogElement>();
const password = ref(''), code = ref(''), error = ref(''), busy = ref(false);
const previous = document.activeElement as HTMLElement | null;
onMounted(async () => { dialog.value?.showModal(); await nextTick(); dialog.value?.querySelector('input')?.focus(); });
onBeforeUnmount(() => { password.value = ''; code.value = ''; dialog.value?.close(); });
onUnmounted(() => { if (previous?.isConnected) previous.focus(); });
async function submit() {
  if (busy.value) return; busy.value = true; error.value = '';
  try { await api('/api/v1/auth/reauthenticate', 'POST', { password: password.value, code: code.value }); await auth.restore(); finishStrong(true); }
  catch (cause) { error.value = errorText(cause); }
  finally { password.value = ''; code.value = ''; busy.value = false; }
}
</script>
<template>
  <dialog ref="dialog" aria-labelledby="strong-heading" class="confirmation-dialog" @cancel.prevent="finishStrong(false)">
    <h2 id="strong-heading">再次认证</h2><p class="muted">敏感操作需要近期的密码和验证器动态码。</p>
    <p v-if="error" class="notice danger" role="alert">{{ error }}</p>
    <form class="form-stack" @submit.prevent="submit">
      <label>当前密码<input v-model="password" type="password" autocomplete="current-password" required maxlength="256"></label>
      <label>验证器动态码<input v-model="code" inputmode="numeric" autocomplete="one-time-code" required pattern="[0-9]{6}" maxlength="6"></label>
      <p class="muted">已使用的动态码不能重复验证，请等待下一组。</p>
      <div class="actions"><button type="button" @click="finishStrong(false)">取消</button><button class="primary" :disabled="busy">{{ busy ? '验证中…' : '确认身份' }}</button></div>
    </form>
  </dialog>
</template>
