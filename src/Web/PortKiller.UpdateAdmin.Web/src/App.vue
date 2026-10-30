<script setup lang="ts">
import { computed, h, nextTick, onBeforeUnmount, onMounted, provide, ref, watch } from 'vue';
import { ConfigProvider, Menu, theme } from 'ant-design-vue';
import { RouterLink, RouterView, useRoute, useRouter } from 'vue-router';
import StrongAuthentication from './components/StrongAuthentication.vue';
import { useAuth } from './state/auth';
import { errorText } from './api/http';
import { appearanceKey, type ThemePreference } from './state/appearance';

const auth = useAuth();
const route = useRoute();
const router = useRouter();
const themePreference = ref<ThemePreference>('system');
const systemTheme = window.matchMedia('(prefers-color-scheme: dark)');
const systemDark = ref(systemTheme.matches);
const dark = computed(() => themePreference.value === 'system' ? systemDark.value : themePreference.value === 'dark');
const error = ref(''), accountMenuOpen = ref(false);
const userMenuRef = ref<HTMLElement | null>(null);
const accountTrigger = ref<HTMLButtonElement | null>(null), accountMenu = ref<HTMLElement | null>(null);
provide('portkill.dark', dark);
provide(appearanceKey, { dark, preference: themePreference, setPreference });
let sessionTimer: ReturnType<typeof setInterval> | undefined, lastForbidden = -Infinity;
let redirectingToLogin = false;
try {
  const stored = localStorage.getItem('portkill.theme');
  if (stored === 'light' || stored === 'dark' || stored === 'system') themePreference.value = stored;
} catch { /* Optional preference. */ }
const entries = computed(() => [
  { id: 'overview', title: '运行总览', permission: 'release.read', path: 'M3 4h18v7H3z M3 14h18v7H3z' },
  { id: 'upload', title: '上传版本', permission: 'release.upload', path: 'M12 16V3 M7 8l5-5 5 5 M4 16v5h16v-5' },
  { id: 'history', title: '发布历史', permission: 'release.read', path: 'M3 12a9 9 0 1 0 3-6 M3 3v6h6 M12 7v5l3 2' },
  { id: 'clients', title: '客户端版本', permission: 'release.read', path: 'M3 4h18v13H3z M8 21h8 M12 17v4' },
  { id: 'security', title: '安全管理', permission: '', path: 'M12 21s7-3.2 7-9V5.6L12 3 5 5.6V12c0 5.8 7 9 7 9Z M9 12l2 2 4-4' },
  { id: 'audit', title: '审计记录', permission: 'audit.read', path: 'M6 3h12v18H6z M9 8h6 M9 12h6 M9 16h4' },
].filter(entry => !entry.permission || auth.can(entry.permission)));
const selected = computed(() => route.name === 'job' ? 'history' : String(route.name ?? 'overview'));
const menuItems = computed(() => entries.value.map(entry => ({
  key: entry.id,
  label: h(RouterLink, { to: '/' + entry.id, class: 'nav-item', 'aria-current': selected.value === entry.id ? 'page' : undefined },
    { default: () => [h('svg', { viewBox: '0 0 24 24', 'aria-hidden': 'true' }, [h('path', { d: entry.path })]), h('span', entry.title)] }),
})));
const title = computed(() => route.name === 'job' ? '任务详情' : entries.value.find(entry => entry.id === selected.value)?.title ?? '运行总览');
const screen = computed(() => route.name === 'overview' && route.query.display === 'screen');
const viewKey = computed(() => route.name === 'job' ? route.fullPath : String(route.name ?? route.path));
const isAdminRoute = computed(() => route.meta.requiresAuth === true);
async function refresh() { error.value = ''; try { await auth.restore(); } catch (cause) { error.value = errorText(cause); } }
async function logout() {
  closeAccountMenu(true);
  try { await auth.logout(); await router.replace('/login'); } catch (cause) { error.value = errorText(cause); }
}
function openReauth() {
  closeAccountMenu(true);
  auth.reauthOpen.value = true;
}
function setPreference(value: ThemePreference) {
  themePreference.value = value;
  try { localStorage.setItem('portkill.theme', value); } catch { /* Optional preference. */ }
}
function changeTheme(value: boolean | string | number) { setPreference(value ? 'dark' : 'light'); }
function onSystemTheme() { systemDark.value = systemTheme.matches; }
function accountItems() { return Array.from(accountMenu.value?.querySelectorAll<HTMLButtonElement>('[role="menuitem"]:not(:disabled)') ?? []); }
async function openAccountMenu(last = false) {
  accountMenuOpen.value = true;
  await nextTick();
  if (!accountMenuOpen.value) return;
  const items = accountItems();
  (last ? items.at(-1) : items[0])?.focus();
}
function closeAccountMenu(restoreFocus = false) {
  const focusedItem = accountMenu.value?.contains(document.activeElement);
  accountMenuOpen.value = false;
  if (restoreFocus || focusedItem) accountTrigger.value?.focus();
}
function toggleAccountMenu() { if (accountMenuOpen.value) closeAccountMenu(true); else void openAccountMenu(); }
function onAccountTriggerKey(event: KeyboardEvent) {
  if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
    event.preventDefault(); void openAccountMenu(event.key === 'ArrowUp');
  } else if (event.key === 'Escape' && accountMenuOpen.value) {
    event.preventDefault(); closeAccountMenu(true);
  }
}
function onAccountMenuKey(event: KeyboardEvent) {
  if (event.key === 'Escape') {
    event.preventDefault(); event.stopPropagation(); closeAccountMenu(true);
  } else if (event.key === 'Tab') {
    // Let the browser move to the previous/next control from the trigger.
    closeAccountMenu(true);
  } else if (['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) {
    event.preventDefault();
    const items = accountItems(), index = items.findIndex(item => item === document.activeElement);
    const next = event.key === 'Home' ? 0 : event.key === 'End' ? items.length - 1
      : (index + (event.key === 'ArrowDown' ? 1 : -1) + items.length) % items.length;
    items[next]?.focus();
  }
}
function onAccountFocusOut(event: FocusEvent) {
  if (!userMenuRef.value?.contains(event.relatedTarget as Node | null)) closeAccountMenu();
}
function onDocumentClick(event: MouseEvent) {
  if (!accountMenuOpen.value) return;
  const target = event.target as Node | null;
  if (userMenuRef.value && target && !userMenuRef.value.contains(target)) {
    closeAccountMenu();
  }
}
function onFocus() { if (!document.hidden && auth.session.value) void auth.ensure().catch(() => {}); }
function onForbidden() {
  if (performance.now() - lastForbidden < 5000) return;
  lastForbidden = performance.now(); void auth.restore().catch(() => {});
}
function onUnauthorized() {
  if (redirectingToLogin || !isAdminRoute.value) return;
  redirectingToLogin = true;
  void router.replace({ path: '/login', query: { returnTo: route.fullPath } }).finally(() => { redirectingToLogin = false; });
}
watch(() => route.fullPath, async () => { accountMenuOpen.value = false; await nextTick(); document.getElementById('main-content')?.focus(); });
watch(auth.session, value => {
  if (!value && isAdminRoute.value) onUnauthorized();
  else if (value?.phase === 'full' && route.meta.permission && !auth.can(String(route.meta.permission))) void router.replace('/security');
});
onMounted(() => {
  systemTheme.addEventListener('change', onSystemTheme);
  onFocus(); sessionTimer = setInterval(onFocus, 30_000);
  window.addEventListener('focus', onFocus); document.addEventListener('visibilitychange', onFocus);
  window.addEventListener('admin:unauthorized', onUnauthorized); window.addEventListener('admin:forbidden', onForbidden);
  document.addEventListener('click', onDocumentClick);
});
onBeforeUnmount(() => {
  systemTheme.removeEventListener('change', onSystemTheme);
  clearInterval(sessionTimer); window.removeEventListener('focus', onFocus); document.removeEventListener('visibilitychange', onFocus);
  window.removeEventListener('admin:unauthorized', onUnauthorized); window.removeEventListener('admin:forbidden', onForbidden);
  document.removeEventListener('click', onDocumentClick);
});
</script>
<template>
  <ConfigProvider :wave="{ disabled: true }" :theme="{ algorithm: dark ? theme.darkAlgorithm : theme.defaultAlgorithm, token: { colorPrimary: '#2563eb', borderRadius: 6 } }">
    <div v-if="isAdminRoute" class="workspace" :class="{ dark, 'monitor-screen': screen }">
      <a class="skip-link" href="#main-content">跳到主内容</a>
      <aside v-if="!screen" class="sidebar" aria-label="主导航">
        <div class="sidebar-header">
          <RouterLink class="brand" to="/overview" aria-label="PortKill 发布管理首页">
            <span class="brand-text">PortKiller<small>更新管理</small></span>
          </RouterLink>
        </div>
        <div class="sidebar-body">
          <span class="nav-label">工作空间</span>
          <Menu mode="inline" :selected-keys="[selected]" :items="menuItems" class="sidebar-menu" />
          <div class="sidebar-footer">
            <button type="button" class="sidebar-refresh-btn" @click="refresh">刷新会话状态</button>
          </div>
        </div>
      </aside>
      <div class="main-area">
        <header class="topbar">
          <div class="breadcrumb">
            <span>工作空间</span>
            <svg viewBox="0 0 24 24" aria-hidden="true" class="breadcrumb-sep"><path d="M9 18l6-6-6-6" /></svg>
            <h1 class="page-title">{{ title }}</h1>
          </div>
          <div class="topbar-actions">
            <button
              type="button"
              class="theme-icon-btn"
              role="switch"
              aria-label="切换深色主题"
              :aria-checked="dark"
              :title="dark ? '切换为浅色主题' : '切换为深色主题'"
              @click="changeTheme(!dark)"
            >
              <svg v-if="!dark" viewBox="0 0 24 24" aria-hidden="true">
                <circle cx="12" cy="12" r="4" />
                <path d="M12 2v2M12 20v2M4.93 4.93l1.41 1.41M17.66 17.66l1.41 1.41M2 12h2M20 12h2M6.34 17.66l-1.41 1.41M19.07 4.93l-1.41 1.41" />
              </svg>
              <svg v-else viewBox="0 0 24 24" aria-hidden="true">
                <path d="M21 12.79A9 9 0 1 1 11.21 3 7 7 0 0 0 21 12.79z" />
              </svg>
            </button>

            <div v-if="auth.session.value" ref="userMenuRef" class="user-menu-wrap" @focusout="onAccountFocusOut">
              <button
                id="account-menu-trigger"
                ref="accountTrigger"
                type="button"
                class="avatar-circle-btn"
                aria-label="账户菜单"
                aria-haspopup="menu"
                aria-controls="account-menu-actions"
                :aria-expanded="accountMenuOpen"
                :title="`当前账号：${auth.session.value.userName}`"
                @click="toggleAccountMenu"
                @keydown="onAccountTriggerKey"
              >
                {{ auth.session.value.userName.slice(0, 1).toUpperCase() }}
              </button>

              <div v-show="accountMenuOpen" class="account-dropdown">
                <div class="dropdown-user-header">
                  <span class="dropdown-avatar-lg" aria-hidden="true">{{ auth.session.value.userName.slice(0, 1).toUpperCase() }}</span>
                  <div class="dropdown-user-meta">
                    <span class="identity">{{ auth.session.value.userName }}</span>
                    <span class="dropdown-user-role">管理员会话 · 已认证</span>
                  </div>
                </div>
                <div class="dropdown-divider" aria-hidden="true" />
                <div id="account-menu-actions" ref="accountMenu" role="menu" aria-labelledby="account-menu-trigger" @keydown="onAccountMenuKey">
                  <button v-if="auth.full.value" type="button" role="menuitem" tabindex="-1" class="dropdown-item" @click="openReauth">
                    <svg viewBox="0 0 24 24" aria-hidden="true">
                      <rect x="3" y="11" width="18" height="11" rx="2" ry="2" />
                      <path d="M7 11V7a5 5 0 0 1 10 0v4" />
                    </svg>
                    <span>再次认证</span>
                  </button>
                  <div v-if="auth.full.value" class="dropdown-divider" role="separator" />
                  <button type="button" role="menuitem" tabindex="-1" class="dropdown-item danger" @click="logout">
                    <svg viewBox="0 0 24 24" aria-hidden="true">
                      <path d="M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4" />
                      <polyline points="16 17 21 12 16 7" />
                      <line x1="21" y1="12" x2="9" y2="12" />
                    </svg>
                    <span>退出</span>
                  </button>
                </div>
              </div>
            </div>
          </div>
        </header>
        <main id="main-content" tabindex="-1">
          <p v-if="error" role="alert" class="notice danger">{{ error }} <button @click="refresh">重试连接</button></p>
          <p v-else-if="auth.connectionError.value" role="status" class="notice">会话状态暂时无法刷新，正在重试。</p>
          <div v-if="auth.loading.value" class="empty-workspace" role="status">正在恢复会话…</div>
          <RouterView v-else-if="auth.full.value" v-slot="{ Component }">
            <component :is="Component" :key="viewKey" />
          </RouterView>
        </main>
        <footer v-if="!screen" class="page-footer"><span>PortKill Update Administration</span><span>版本发布工作空间 · 客户端由用户确认下载与安装</span></footer>
      </div>
      <StrongAuthentication v-if="auth.reauthOpen.value" />
    </div>
    <div v-else class="login-shell" :class="{ dark }">
      <RouterView />
    </div>
  </ConfigProvider>
</template>
