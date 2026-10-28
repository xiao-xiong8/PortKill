<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, ref } from 'vue';
import { useRoute } from 'vue-router';
import type { components } from '../api/contracts.generated';
import { api, errorText } from '../api/http';
import { time } from '../api/display';
import PageControls from '../components/PageControls.vue';
import { useAuth } from '../state/auth';
import { SECURITY_ROLES, redact, roleLabel, toggleRole } from './securityModel';

type Account = components['schemas']['AccountView'];
type AccountPage = components['schemas']['PageResponseOfAccountView'];
type SessionPage = components['schemas']['PageResponseOfSessionView'];

const auth = useAuth();
const route = useRoute();
const notice = ref('');
const mutation = ref('');

const accountPage = ref(1), accountData = ref<AccountPage>(), accountBusy = ref(false), accountError = ref('');
const sessionPage = ref(1), sessionData = ref<SessionPage>(), sessionBusy = ref(false), sessionError = ref('');
let accountController: AbortController | undefined;
let sessionController: AbortController | undefined;

const showCreate = ref(false), createUserName = ref(''), createPassword = ref(''), createPasswordAgain = ref('');
const createRoles = ref<string[]>(['Viewer']);
const editingAccount = ref<Account>(), roleDraft = ref<string[]>(), roleDialog = ref<HTMLDialogElement>();
const roleDialogReturnFocus = ref<HTMLElement | null>(null);
const recoveryCodes = ref<string[]>([]);
const currentPassword = ref(''), newPassword = ref(''), newPasswordAgain = ref('');

async function loadAccounts() {
  if (!auth.can('accounts.manage')) return;
  accountController?.abort(); const controller = accountController = new AbortController();
  accountBusy.value = true; accountError.value = '';
  try {
    const result = await api<AccountPage>(`/api/v1/accounts?page=${accountPage.value}&pageSize=20`, 'GET', undefined, controller.signal);
    if (!controller.signal.aborted) accountData.value = result;
  } catch (cause) { if (!controller.signal.aborted) { accountData.value = undefined; accountError.value = errorText(cause); } }
  finally { if (!controller.signal.aborted) accountBusy.value = false; }
}

async function loadSessions() {
  sessionController?.abort(); const controller = sessionController = new AbortController();
  sessionBusy.value = true; sessionError.value = '';
  try {
    const result = await api<SessionPage>(`/api/v1/auth/sessions?page=${sessionPage.value}&pageSize=20`, 'GET', undefined, controller.signal);
    if (!controller.signal.aborted) sessionData.value = result;
  } catch (cause) { if (!controller.signal.aborted) { sessionData.value = undefined; sessionError.value = errorText(cause); } }
  finally { if (!controller.signal.aborted) sessionBusy.value = false; }
}

async function sensitive(label: string, action: () => Promise<void>) {
  if (mutation.value) return;
  mutation.value = label; notice.value = '';
  try {
    if (!(await auth.requireStrong())) return;
    await action();
  } catch (cause) { notice.value = errorText(cause); }
  finally { mutation.value = ''; }
}

function setCreateRole(role: string) { createRoles.value = toggleRole(createRoles.value, role); }
function setRole(role: string) { roleDraft.value = toggleRole(roleDraft.value ?? [], role); }

function resetCreateForm() {
  createUserName.value = ''; createPassword.value = ''; createPasswordAgain.value = '';
  createRoles.value = ['Viewer']; showCreate.value = false;
}

async function createAccount() {
  if (!createRoles.value.length) { notice.value = '至少选择一个角色。'; return; }
  if (createPassword.value !== createPasswordAgain.value) { notice.value = '两次输入的密码不一致。'; return; }
  await sensitive('account-create', async () => {
    await api<components['schemas']['CreatedAccountResponse']>('/api/v1/accounts', 'POST', {
      userName: createUserName.value, password: createPassword.value, roles: createRoles.value,
    });
    resetCreateForm(); accountPage.value = 1; await loadAccounts();
  });
  createPassword.value = ''; createPasswordAgain.value = '';
}

async function openRoles(account: Account) {
  roleDialogReturnFocus.value = document.activeElement as HTMLElement | null;
  editingAccount.value = account; roleDraft.value = [...account.roles];
  await nextTick(); roleDialog.value?.showModal(); roleDialog.value?.querySelector<HTMLElement>('input')?.focus();
}

function closeRoles() {
  roleDialog.value?.close(); editingAccount.value = undefined; roleDraft.value = undefined;
  const target = roleDialogReturnFocus.value; roleDialogReturnFocus.value = null; target?.focus();
}

async function saveRoles() {
  const account = editingAccount.value, roles = roleDraft.value ?? [];
  if (!account) return;
  if (!roles.length) { notice.value = '至少选择一个角色。'; return; }
  await sensitive('account-roles', async () => {
    await api(`/api/v1/accounts/${encodeURIComponent(account.id)}/roles`, 'PUT', { roles });
    closeRoles(); await loadAccounts();
    if (account.id === auth.session.value?.userId) await auth.restore();
  });
}

async function setAccountDisabled(account: Account) {
  const next = !account.disabled;
  if (!window.confirm(`${next ? '停用' : '启用'}账号“${account.userName}”？`)) return;
  await sensitive('account-state', async () => {
    await api(`/api/v1/accounts/${encodeURIComponent(account.id)}/state`, 'PUT', { disabled: next });
    if (account.id === auth.session.value?.userId) await auth.restore(); else await loadAccounts();
  });
}

async function resetMfa(account: Account) {
  if (!window.confirm(`重置账号“${account.userName}”的 MFA？该账号的现有会话会失效。`)) return;
  await sensitive('account-mfa', async () => {
    await api(`/api/v1/accounts/${encodeURIComponent(account.id)}/reset-mfa`, 'POST', {});
    if (account.id === auth.session.value?.userId) await auth.restore(); else await loadAccounts();
  });
}

async function revokeSession(id: string, current: boolean) {
  if (!window.confirm(current ? '撤销当前会话并返回登录页？' : '撤销这个会话？')) return;
  if (mutation.value) return;
  mutation.value = 'session-revoke'; notice.value = '';
  try {
    await api(`/api/v1/auth/sessions/${encodeURIComponent(id)}`, 'DELETE');
    if (current) auth.clear(); else await loadSessions();
  } catch (cause) { notice.value = errorText(cause); }
  finally { mutation.value = ''; }
}

async function changePassword() {
  if (newPassword.value !== newPasswordAgain.value) { notice.value = '两次输入的新密码不一致。'; return; }
  await sensitive('password', async () => {
    await api('/api/v1/auth/password', 'POST', { currentPassword: currentPassword.value, newPassword: newPassword.value });
    currentPassword.value = ''; newPassword.value = ''; newPasswordAgain.value = '';
    auth.clear();
  });
  currentPassword.value = ''; newPassword.value = ''; newPasswordAgain.value = '';
}

async function regenerateRecoveryCodes() {
  if (!window.confirm('重新生成恢复码后，旧恢复码会立即失效。继续？')) return;
  await sensitive('recovery-codes', async () => {
    const result = await api<components['schemas']['RecoveryCodesResponse']>('/api/v1/auth/recovery-codes', 'POST', {});
    recoveryCodes.value = result.recoveryCodes; await loadSessions();
  });
}

function changeAccountPage(page: number) { accountPage.value = page; void loadAccounts(); }
function changeSessionPage(page: number) { sessionPage.value = page; void loadSessions(); }

onMounted(() => { void loadAccounts(); void loadSessions(); });
onBeforeUnmount(() => { accountController?.abort(); sessionController?.abort(); });
</script>

<template>
  <section v-if="route.query.denied" class="empty-workspace" role="alert">
    <h2>无权访问此页面</h2>
    <p class="muted">当前账号没有访问该管理页面所需的权限。</p>
  </section>
  <p v-if="notice" class="notice danger" role="alert">{{ notice }}</p>

  <section v-if="auth.can('accounts.manage')" class="surface" :aria-busy="accountBusy">
    <div class="section-heading"><div><h2>管理员账号</h2><p class="muted">账号变更需要近期强认证；服务端会阻止移除最后一个已完成 MFA 的有效管理员。</p></div><div class="actions"><button :disabled="accountBusy || !!mutation" @click="loadAccounts">刷新</button><button class="primary" :disabled="!!mutation" @click="showCreate = !showCreate">{{ showCreate ? '收起新账号' : '新建账号' }}</button></div></div>
    <form v-if="showCreate" class="surface form-stack nested-surface" @submit.prevent="createAccount">
      <h3>新建管理员账号</h3>
      <div class="inline-fields"><label>账号<input v-model="createUserName" autocomplete="off" required maxlength="64"></label><label>密码<input v-model="createPassword" type="password" autocomplete="new-password" required maxlength="256"></label><label>确认密码<input v-model="createPasswordAgain" type="password" autocomplete="new-password" required maxlength="256"></label></div>
      <fieldset class="role-options"><legend>角色</legend><label v-for="role in SECURITY_ROLES" :key="role" class="check-label"><input type="checkbox" :checked="createRoles.includes(role)" @change="setCreateRole(role)">{{ roleLabel(role) }}</label></fieldset>
      <div class="actions"><button type="button" @click="resetCreateForm">取消</button><button class="primary" :disabled="!!mutation">创建账号</button></div>
    </form>
    <p v-if="accountError" class="notice danger" role="alert">{{ accountError }}</p><p v-else-if="accountBusy" class="muted" role="status">正在加载账号…</p>
    <div v-else-if="accountData?.items.length" class="table-scroll"><table aria-label="管理员账号列表"><thead><tr><th scope="col">账号</th><th scope="col">状态</th><th scope="col">MFA</th><th scope="col">角色</th><th scope="col">操作</th></tr></thead><tbody><tr v-for="account in accountData.items" :key="account.id"><td>{{ account.userName }}<br><span class="muted mono">{{ redact(account.id) }}</span></td><td><span :class="['status-label', account.disabled ? 'failed' : '']">{{ account.disabled ? '已停用' : '有效' }}</span></td><td>{{ account.mfaEnabled ? (account.recoveryAcknowledged ? '已启用' : '待确认恢复码') : '未绑定' }}</td><td>{{ account.roles.map(roleLabel).join('、') }}</td><td><div class="actions"><button class="text-button" :disabled="!!mutation" @click="openRoles(account)">编辑角色</button><button class="text-button" :disabled="!!mutation" @click="setAccountDisabled(account)">{{ account.disabled ? '启用' : '停用' }}</button><button class="text-button danger-button" :disabled="!!mutation" @click="resetMfa(account)">重置 MFA</button></div></td></tr></tbody></table></div>
    <div v-else-if="!accountBusy" class="empty-workspace"><h3>暂无账号</h3><p class="muted">创建第一个管理账号后，它会显示在这里。</p></div>
    <PageControls :page="accountPage" :total="accountData?.total" :busy="accountBusy || !!mutation" @change="changeAccountPage" />
  </section>

  <section class="surface" :aria-busy="sessionBusy">
    <div class="section-heading"><div><h2>当前账号会话</h2><p class="muted">撤销其他浏览器会话会立即生效；撤销当前会话后将返回登录页。</p></div><button :disabled="sessionBusy || !!mutation" @click="loadSessions">刷新</button></div>
    <p v-if="sessionError" class="notice danger" role="alert">{{ sessionError }}</p><p v-else-if="sessionBusy" class="muted" role="status">正在加载会话…</p>
    <div v-else-if="sessionData?.items.length" class="table-scroll"><table aria-label="当前账号会话列表"><thead><tr><th scope="col">会话</th><th scope="col">认证方式</th><th scope="col">阶段</th><th scope="col">创建时间</th><th scope="col">最近活动</th><th scope="col">到期</th><th scope="col">操作</th></tr></thead><tbody><tr v-for="item in sessionData.items" :key="item.id"><td><span class="mono">{{ redact(item.id) }}</span><span v-if="item.current" class="status-label"> · 当前</span></td><td>{{ item.authenticationMethod === 'totp' ? '密码 + MFA' : item.authenticationMethod === 'recovery' ? '恢复码' : '密码' }}</td><td>{{ item.phase === 'full' ? '完整会话' : item.phase }}</td><td>{{ time(item.createdUtcMs) }}</td><td>{{ time(item.lastSeenUtcMs) }}</td><td>{{ time(item.expiresUtcMs) }}</td><td><button class="text-button danger-button" :disabled="!!mutation" @click="revokeSession(item.id, item.current)">撤销</button></td></tr></tbody></table></div>
    <div v-else-if="!sessionBusy" class="empty-workspace"><h3>暂无有效会话</h3></div>
    <PageControls :page="sessionPage" :total="sessionData?.total" :busy="sessionBusy || !!mutation" @change="changeSessionPage" />
  </section>

  <section class="surface"><div class="section-heading"><div><h2>账号安全</h2><p class="muted">密码和恢复码只在本次操作期间保留在内存中。</p></div></div>
    <div class="split-workspace security-forms">
      <form class="form-stack" @submit.prevent="changePassword"><h3>修改密码</h3><label>当前密码<input v-model="currentPassword" type="password" autocomplete="current-password" required maxlength="256"></label><label>新密码<input v-model="newPassword" type="password" autocomplete="new-password" required maxlength="256"></label><label>确认新密码<input v-model="newPasswordAgain" type="password" autocomplete="new-password" required maxlength="256"></label><button class="primary" :disabled="!!mutation">修改密码并退出</button></form>
      <div class="form-stack"><h3>恢复码</h3><p class="muted">恢复码只能使用一次。重新生成会让旧恢复码立即失效，并撤销其他会话。</p><button :disabled="!!mutation" @click="regenerateRecoveryCodes">重新生成恢复码</button><pre v-if="recoveryCodes.length" class="recovery-codes" aria-label="新的恢复码">{{ recoveryCodes.join('\n') }}</pre><p v-if="recoveryCodes.length" class="notice">恢复码只显示在当前页面内，请保存到安全位置后关闭页面。</p></div>
    </div>
  </section>

  <dialog v-if="editingAccount" ref="roleDialog" class="confirmation-dialog" aria-labelledby="role-heading" @cancel.prevent="closeRoles">
    <h2 id="role-heading">编辑角色</h2><p class="muted">账号：{{ editingAccount.userName }}。至少保留一个角色。</p>
    <form class="form-stack" @submit.prevent="saveRoles"><fieldset class="role-options"><legend>角色</legend><label v-for="role in SECURITY_ROLES" :key="role" class="check-label"><input type="checkbox" :checked="roleDraft?.includes(role)" @change="setRole(role)">{{ roleLabel(role) }}</label></fieldset><div class="actions"><button type="button" @click="closeRoles">取消</button><button class="primary" :disabled="!!mutation">保存角色</button></div></form>
  </dialog>
</template>
