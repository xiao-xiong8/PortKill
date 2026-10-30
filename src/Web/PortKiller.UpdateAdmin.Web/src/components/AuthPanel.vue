<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, reactive, ref, watch } from 'vue';
import { api, errorText } from '../api/http';
import type { components } from '../api/contracts.generated';
import { useAuth } from '../state/auth';

const auth = useAuth();
const userName = ref(''), password = ref(''), code = ref(''), recovery = ref(false), acknowledged = ref(false);
const secret = ref<components['schemas']['EnrollmentSecret'] | null>(null), codes = ref<string[]>([]);
const busy = ref(false), error = ref('');
const passwordVisible = ref(false), formRef = ref<HTMLFormElement | null>(null);
const fieldErrors = reactive({ userName: '', password: '', code: '', acknowledged: '' });

const phase = computed(() => auth.session.value?.phase);
const heading = computed(() =>
  !phase.value
    ? '登录发布管理'
    : phase.value === 'enrollmentRequired'
      ? '绑定验证器'
      : phase.value === 'recoveryAcknowledgementRequired'
        ? '保存恢复码'
        : '验证登录'
);

function clearFieldErrors() {
  fieldErrors.userName = '';
  fieldErrors.password = '';
  fieldErrors.code = '';
  fieldErrors.acknowledged = '';
}

watch(phase, value => {
  error.value = '';
  passwordVisible.value = false;
  recovery.value = false;
  code.value = '';
  if (value !== 'enrollmentRequired') secret.value = null;
  if (value !== 'recoveryAcknowledgementRequired') { codes.value = []; acknowledged.value = false; }
  clearFieldErrors();
});

function validateField(field: 'userName' | 'password' | 'code' | 'acknowledged'): boolean {
  if (field === 'userName') {
    fieldErrors.userName = userName.value.trim() ? '' : '请输入管理员账号';
    return !fieldErrors.userName;
  }
  if (field === 'password') {
    fieldErrors.password = password.value ? '' : '请输入登录密码';
    return !fieldErrors.password;
  }
  if (field === 'code') {
    const trimmed = code.value.trim();
    if (!trimmed) {
      fieldErrors.code = recovery.value ? '请输入恢复码' : '请输入 6 位动态验证码';
    } else if (!recovery.value && !/^[0-9]{6}$/.test(trimmed)) {
      fieldErrors.code = '验证码须为 6 位数字';
    } else {
      fieldErrors.code = '';
    }
    return !fieldErrors.code;
  }
  if (field === 'acknowledged') {
    fieldErrors.acknowledged = acknowledged.value ? '' : '请勾选确认已安全保存恢复码';
    return !fieldErrors.acknowledged;
  }
  return true;
}

function validateForm(): boolean {
  if (!phase.value) {
    const u = validateField('userName');
    const p = validateField('password');
    return u && p;
  }
  if (phase.value === 'enrollmentRequired') {
    return validateField('code');
  }
  if (phase.value === 'recoveryAcknowledgementRequired') {
    return validateField('acknowledged');
  }
  return validateField('code');
}

async function submit() {
  if (busy.value) return;
  error.value = '';
  if (!validateForm()) {
    await nextTick();
    formRef.value?.querySelector<HTMLInputElement>('[aria-invalid="true"]')?.focus();
    return;
  }
  busy.value = true;
  try {
    if (!phase.value) {
      await api('/api/v1/auth/login', 'POST', { userName: userName.value.trim(), password: password.value });
    } else if (phase.value === 'enrollmentRequired') {
      const result = await api<components['schemas']['RecoveryCodesResponse']>('/api/v1/auth/mfa/enrollment/confirm', 'POST', { code: code.value.trim() });
      codes.value = result.recoveryCodes;
      secret.value = null;
    } else if (phase.value === 'recoveryAcknowledgementRequired') {
      await api('/api/v1/auth/mfa/enrollment/acknowledge', 'POST', { acknowledged: acknowledged.value });
      codes.value = [];
    } else {
      await api(`/api/v1/auth/mfa/${recovery.value ? 'recovery' : 'verify'}`, 'POST', { code: code.value.trim() });
    }
    await auth.restore();
  } catch (cause) {
    error.value = errorText(cause);
  } finally {
    busy.value = false;
    password.value = '';
    passwordVisible.value = false;
    code.value = '';
  }
}

async function startEnrollment() {
  if (busy.value) return;
  busy.value = true;
  error.value = '';
  try {
    secret.value = await api('/api/v1/auth/mfa/enrollment/start', 'POST', {});
  } catch (cause) {
    error.value = errorText(cause);
  } finally {
    busy.value = false;
  }
}

async function backToLogin() {
  if (busy.value) return;
  busy.value = true;
  try { await auth.logout(); }
  catch (cause) { error.value = errorText(cause); }
  finally { busy.value = false; }
}

onBeforeUnmount(() => {
  secret.value = null;
  codes.value = [];
  password.value = '';
  code.value = '';
});
</script>

<template>
  <section class="auth-panel login" aria-labelledby="auth-heading">
    <div class="auth-header">
      <p class="login-kicker">{{ !phase ? 'WELCOME BACK' : 'SECURE YOUR ACCOUNT' }}</p>
      <h1 id="auth-heading">{{ heading }}</h1>
      <p class="auth-subtitle">
        {{ !phase ? 'PortKiller 更新分发与签名管理系统' : '完成多因素认证后访问发布工作空间' }}
      </p>
    </div>

    <p v-if="error" class="auth-alert" role="alert">{{ error }}</p>

    <form ref="formRef" class="auth-form" novalidate :aria-busy="busy" @submit.prevent="submit">
      <template v-if="!phase">
        <div class="auth-field field">
          <label for="auth-username">账号</label>
          <div class="input-wrap">
          <svg class="icon input-icon" aria-hidden="true"><use href="#login-i-user" /></svg>
          <input
            id="auth-username"
            v-model="userName"
            name="username"
            autocomplete="username"
            placeholder="请输入管理员账号"
            required
            maxlength="100"
            autofocus
            :disabled="busy"
            aria-describedby="auth-username-error"
            :class="{ 'is-invalid': Boolean(fieldErrors.userName) }"
            :aria-invalid="Boolean(fieldErrors.userName)"
            @input="fieldErrors.userName && validateField('userName')"
            @blur="validateField('userName')"
          >
          </div>
          <span v-if="fieldErrors.userName" id="auth-username-error" class="field-error">{{ fieldErrors.userName }}</span>
        </div>

        <div class="auth-field field">
          <label for="auth-password">密码</label>
          <div class="input-wrap">
          <svg class="icon input-icon" aria-hidden="true"><use href="#login-i-lock" /></svg>
          <input
            id="auth-password"
            v-model="password"
            name="password"
            :type="passwordVisible ? 'text' : 'password'"
            autocomplete="current-password"
            placeholder="请输入登录密码"
            required
            maxlength="256"
            :disabled="busy"
            aria-describedby="auth-password-error"
            :class="{ 'is-invalid': Boolean(fieldErrors.password) }"
            :aria-invalid="Boolean(fieldErrors.password)"
            @input="fieldErrors.password && validateField('password')"
            @blur="validateField('password')"
          >
          <button type="button" class="password-toggle" :disabled="busy" :aria-label="passwordVisible ? '隐藏密码' : '显示密码'"
            :title="passwordVisible ? '隐藏密码' : '显示密码'" :aria-pressed="passwordVisible" @click="passwordVisible = !passwordVisible">
            <svg class="icon" aria-hidden="true"><use :href="passwordVisible ? '#login-i-eye-off' : '#login-i-eye'" /></svg>
          </button>
          </div>
          <span v-if="fieldErrors.password" id="auth-password-error" class="field-error">{{ fieldErrors.password }}</span>
        </div>
      </template>

      <template v-else-if="phase === 'enrollmentRequired'">
        <p class="auth-hint">请在身份验证器中添加 PortKill 账户，并输入下方生成的密钥。</p>
        <button v-if="!secret" type="button" class="auth-secondary-btn" :disabled="busy" @click="startEnrollment">
          生成绑定密钥
        </button>
        <div v-else class="auth-field field">
          <label for="auth-secret-key">绑定密钥</label>
          <input
            id="auth-secret-key"
            :value="secret.sharedKey"
            readonly
            autocomplete="off"
            spellcheck="false"
            class="mono"
          >
          <span class="field-hint">此密钥仅用于绑定验证器，请妥善保管。</span>
        </div>

        <div class="auth-field field">
          <label for="auth-enroll-code">验证器动态码</label>
          <input
            id="auth-enroll-code"
            v-model="code"
            inputmode="numeric"
            autocomplete="one-time-code"
            placeholder="请输入 6 位数字动态码"
            required
            pattern="[0-9]{6}"
            maxlength="6"
            :disabled="busy"
            aria-describedby="auth-enroll-code-error"
            :class="{ 'is-invalid': Boolean(fieldErrors.code) }"
            :aria-invalid="Boolean(fieldErrors.code)"
            @input="fieldErrors.code && validateField('code')"
            @blur="validateField('code')"
          >
          <span v-if="fieldErrors.code" id="auth-enroll-code-error" class="field-error">{{ fieldErrors.code }}</span>
        </div>
      </template>

      <template v-else-if="phase === 'recoveryAcknowledgementRequired'">
        <p class="auth-hint">每个恢复码仅可使用一次，请将其保存至安全位置后继续。</p>
        <pre v-if="codes.length" class="recovery-codes">{{ codes.join('\n') }}</pre>
        <p v-else class="notice">恢复码只显示一次。若尚未保存，请联系管理员重置 MFA 后重新绑定。</p>
        <div class="auth-field field">
          <label class="check-label">
            <input
              v-model="acknowledged"
              type="checkbox"
              required
              :disabled="busy"
              :aria-invalid="Boolean(fieldErrors.acknowledged)"
              aria-describedby="auth-ack-error"
              @change="fieldErrors.acknowledged && validateField('acknowledged')"
            >
            我已安全保存恢复码
          </label>
          <span v-if="fieldErrors.acknowledged" id="auth-ack-error" class="field-error">{{ fieldErrors.acknowledged }}</span>
        </div>
      </template>

      <template v-else>
        <div class="auth-field field">
          <div class="auth-label-row">
            <label for="auth-verify-code">{{ recovery ? '恢复码' : '验证器动态码' }}</label>
            <button
              type="button"
              class="text-button auth-switch-mode"
              :disabled="busy"
              @click="recovery = !recovery; code = ''; fieldErrors.code = ''"
            >
              {{ recovery ? '使用验证器动态码' : '使用恢复码登录' }}
            </button>
          </div>
          <input
            id="auth-verify-code"
            v-model="code"
            :inputmode="recovery ? 'text' : 'numeric'"
            autocomplete="one-time-code"
            :placeholder="recovery ? '请输入一次性恢复码' : '请输入 6 位数字动态码'"
            required
            :maxlength="recovery ? 100 : 6"
            :disabled="busy"
            aria-describedby="auth-verify-code-error"
            :class="{ 'is-invalid': Boolean(fieldErrors.code) }"
            :aria-invalid="Boolean(fieldErrors.code)"
            @input="fieldErrors.code && validateField('code')"
            @blur="validateField('code')"
          >
          <span v-if="fieldErrors.code" id="auth-verify-code-error" class="field-error">{{ fieldErrors.code }}</span>
        </div>
      </template>

      <button
        type="submit"
        class="auth-submit-btn login-submit"
        :disabled="busy || (phase === 'enrollmentRequired' && !secret)"
      >
        <span>{{ busy ? '正在验证…' : !phase ? '登录' : phase === 'recoveryAcknowledgementRequired' ? '已保存，进入工作空间' : '验证并继续' }}</span>
        <svg v-if="!phase && !busy" class="icon" aria-hidden="true"><use href="#login-i-arrow" /></svg>
      </button>

      <p v-if="!phase" class="auth-hint-after">登录后按提示完成身份验证</p>
      <button v-if="phase" type="button" class="text-button auth-back-btn" :disabled="busy" @click="backToLogin">
        返回登录
      </button>
    </form>
  </section>
</template>
