<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from 'vue';
import { api, errorText } from '../api/http';
import type { components } from '../api/contracts.generated';
import { useAuth } from '../state/auth';
const auth = useAuth();
const userName = ref(''), password = ref(''), code = ref(''), recovery = ref(false), acknowledged = ref(false);
const secret = ref<components['schemas']['EnrollmentSecret'] | null>(null), codes = ref<string[]>([]);
const busy = ref(false), error = ref('');
const phase = computed(() => auth.session.value?.phase);
const heading = computed(() => !phase.value ? '登录发布管理' : phase.value === 'enrollmentRequired' ? '绑定验证器' : phase.value === 'recoveryAcknowledgementRequired' ? '保存恢复码' : '验证登录');
async function submit() {
  if (busy.value) return; busy.value = true; error.value = '';
  try {
    if (!phase.value) await api('/api/v1/auth/login', 'POST', { userName: userName.value, password: password.value });
    else if (phase.value === 'enrollmentRequired') {
      const result = await api<components['schemas']['RecoveryCodesResponse']>('/api/v1/auth/mfa/enrollment/confirm', 'POST', { code: code.value });
      codes.value = result.recoveryCodes; secret.value = null;
    } else if (phase.value === 'recoveryAcknowledgementRequired') {
      await api('/api/v1/auth/mfa/enrollment/acknowledge', 'POST', { acknowledged: acknowledged.value }); codes.value = [];
    } else await api(`/api/v1/auth/mfa/${recovery.value ? 'recovery' : 'verify'}`, 'POST', { code: code.value });
    await auth.restore();
  } catch (cause) { error.value = errorText(cause); }
  finally { busy.value = false; password.value = ''; code.value = ''; }
}
async function startEnrollment() {
  busy.value = true; error.value = '';
  try { secret.value = await api('/api/v1/auth/mfa/enrollment/start', 'POST', {}); }
  catch (cause) { error.value = errorText(cause); }
  finally { busy.value = false; }
}
onBeforeUnmount(() => { secret.value = null; codes.value = []; password.value = ''; code.value = ''; });
</script>
<template>
  <section class="auth-panel" aria-labelledby="auth-heading">
    <p class="eyebrow">PORTKILL / 发布管理</p><h1 id="auth-heading">{{ heading }}</h1>
    <p class="muted">{{ !phase ? '使用你的管理账号继续。' : '完成多因素认证后访问发布工作空间。' }}</p>
    <p v-if="error" class="notice danger" role="alert">{{ error }}</p>
    <form class="form-stack" @submit.prevent="submit">
      <template v-if="!phase">
        <label>账号<input v-model="userName" name="username" autocomplete="username" required maxlength="100" autofocus></label>
        <label>密码<input v-model="password" name="password" type="password" autocomplete="current-password" required maxlength="256"></label>
      </template>
      <template v-else-if="phase === 'enrollmentRequired'">
        <p>在验证器中手动添加 PortKill 账户，输入下面的密钥。</p>
        <button v-if="!secret" type="button" :disabled="busy" @click="startEnrollment">生成绑定密钥</button>
        <template v-else><label>绑定密钥<input :value="secret.sharedKey" readonly autocomplete="off" spellcheck="false" class="mono"></label><p class="muted">此密钥只用于绑定验证器，请妥善保管。</p></template>
        <label>验证器动态码<input v-model="code" inputmode="numeric" autocomplete="one-time-code" required pattern="[0-9]{6}" maxlength="6"></label>
      </template>
      <template v-else-if="phase === 'recoveryAcknowledgementRequired'">
        <p>每个恢复码只能使用一次。保存到安全位置后继续。</p>
        <pre v-if="codes.length" class="recovery-codes">{{ codes.join('\n') }}</pre>
        <p v-else class="notice">恢复码只显示一次。若尚未保存，请联系管理员重置 MFA 后重新绑定。</p>
        <label class="check-label"><input v-model="acknowledged" type="checkbox" required>我已安全保存恢复码</label>
      </template>
      <template v-else>
        <label>{{ recovery ? '恢复码' : '验证器动态码' }}<input v-model="code" :inputmode="recovery ? 'text' : 'numeric'" autocomplete="one-time-code" required :maxlength="recovery ? 100 : 6"></label>
        <button type="button" class="text-button" @click="recovery = !recovery; code = ''">{{ recovery ? '使用验证器动态码' : '使用恢复码登录' }}</button>
      </template>
      <button class="primary" :disabled="busy || (phase === 'enrollmentRequired' && !secret)"> {{ busy ? '正在验证…' : !phase ? '登录' : phase === 'recoveryAcknowledgementRequired' ? '已保存，进入工作空间' : '验证并继续' }} </button>
      <button v-if="phase" type="button" class="text-button" :disabled="busy" @click="auth.logout()">返回登录</button>
    </form>
  </section>
</template>
