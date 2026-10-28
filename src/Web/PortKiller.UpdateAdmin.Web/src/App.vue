<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { ConfigProvider, Switch, theme } from 'ant-design-vue';
import { RouterLink, RouterView, useRoute, useRouter } from 'vue-router';
import StrongAuthentication from './components/StrongAuthentication.vue';
import { useAuth } from './state/auth';
import { errorText } from './api/http';

const auth = useAuth();
const route = useRoute();
const router = useRouter();
const dark = ref(false), error = ref('');
let redirectingToLogin = false;
try { dark.value = localStorage.getItem('portkill.theme') === 'dark'; } catch { /* Optional preference. */ }
const entries = computed(() => [
  { id: 'overview', title: '发布概览', permission: 'release.read', path: 'M3 3h7v7H3z M14 3h7v7h-7z M3 14h7v7H3z M14 14h7v7h-7z' },
  { id: 'upload', title: '上传版本', permission: 'release.upload', path: 'M12 16V3 M7 8l5-5 5 5 M4 16v5h16v-5' },
  { id: 'history', title: '发布历史', permission: 'release.read', path: 'M3 12a9 9 0 1 0 3-6 M3 3v6h6 M12 7v5l3 2' },
  { id: 'clients', title: '客户端版本', permission: 'release.read', path: 'M3 4h18v13H3z M8 21h8 M12 17v4' },
  { id: 'security', title: '安全管理', permission: '', path: 'M12 3l8 3v6c0 5-8 9-8 9s-8-4-8-9V6z M9 12l2 2 4-4' },
  { id: 'audit', title: '审计记录', permission: 'audit.read', path: 'M6 3h12v18H6z M9 8h6 M9 12h6 M9 16h4' },
].filter(entry => !entry.permission || auth.can(entry.permission)));
const selected = computed(() => route.name === 'upload-resume' ? 'upload' : String(route.name ?? 'overview'));
const title = computed(() => selected.value === 'job' ? '任务详情' : entries.value.find(entry => entry.id === selected.value)?.title ?? '发布概览');
const isAdminRoute = computed(() => route.meta.requiresAuth === true);
async function refresh() { error.value = ''; try { await auth.restore(); } catch (cause) { error.value = errorText(cause); } }
async function logout() { try { await auth.logout(); await router.replace('/login'); } catch (cause) { error.value = errorText(cause); } }
function changeTheme(value: boolean | string | number) { dark.value = !!value; try { localStorage.setItem('portkill.theme', dark.value ? 'dark' : 'light'); } catch { /* Optional preference. */ } }
function onFocus() { if (auth.session.value) void refresh(); }
function onUnauthorized() {
  if (redirectingToLogin || route.path === '/login') return;
  redirectingToLogin = true;
  void router.replace({ path: '/login', query: { returnTo: route.fullPath } }).finally(() => { redirectingToLogin = false; });
}
watch(() => route.fullPath, async () => { await nextTick(); document.getElementById('main-content')?.focus(); });
onMounted(() => { void refresh(); window.addEventListener('focus', onFocus); window.addEventListener('admin:unauthorized', onUnauthorized); });
onBeforeUnmount(() => { window.removeEventListener('focus', onFocus); window.removeEventListener('admin:unauthorized', onUnauthorized); });
</script>
<template>
  <ConfigProvider :theme="{ algorithm: dark ? theme.darkAlgorithm : theme.defaultAlgorithm, token: { colorPrimary: '#2563eb', borderRadius: 6 } }">
    <div v-if="isAdminRoute" class="workspace" :class="{ dark }">
      <a class="skip-link" href="#main-content">跳到主内容</a>
      <aside class="sidebar" aria-label="主导航">
        <RouterLink class="brand" to="/overview" aria-label="PortKill 发布管理首页"><span class="brand-mark" aria-hidden="true">P</span><span>PortKill<small>发布管理</small></span></RouterLink>
        <span class="nav-label">工作空间</span>
        <nav><RouterLink v-for="entry in entries" :key="entry.id" :to="'/' + entry.id" class="nav-item" :class="{ active: selected === entry.id }" :aria-current="selected === entry.id ? 'page' : undefined"><svg viewBox="0 0 24 24" aria-hidden="true"><path :d="entry.path" /></svg>{{ entry.title }}</RouterLink></nav>
        <div class="sidebar-footer">PortKill · stable<br>Windows x64</div>
      </aside>
      <div class="main-area">
        <header class="topbar"><div class="breadcrumb"><span>工作空间</span><span aria-hidden="true">/</span><strong>PortKill 管理</strong></div>
          <div class="topbar-actions"><span v-if="auth.session.value" class="identity">{{ auth.session.value.userName }}</span><Switch :checked="dark" aria-label="切换深色主题" @change="changeTheme" /><button v-if="auth.full.value" class="text-button" @click="auth.reauthOpen.value = true">再次认证</button><button v-if="auth.session.value" class="text-button" @click="logout">退出</button></div>
        </header>
        <main id="main-content" tabindex="-1">
          <p v-if="error" role="alert" class="notice danger">{{ error }} <button @click="refresh">重试连接</button></p>
          <div v-if="auth.loading.value" class="empty-workspace" role="status">正在恢复会话…</div>
          <template v-else-if="auth.full.value"><div class="page-heading"><div><p class="eyebrow">PORTKILL / STABLE</p><h1>{{ title }}</h1><p class="muted">客户端由用户确认下载与安装。</p></div><button @click="refresh">刷新会话</button></div><RouterView /></template>
        </main>
        <footer class="page-footer"><span>PortKill Update Administration</span><span>版本发布工作空间</span></footer>
      </div>
      <StrongAuthentication v-if="auth.reauthOpen.value" />
    </div>
    <div v-else class="login-shell" :class="{ dark }"><div class="login-toolbar"><Switch :checked="dark" aria-label="切换深色主题" @change="changeTheme" /></div><RouterView /></div>
  </ConfigProvider>
</template>
