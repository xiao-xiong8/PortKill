<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { Button, ConfigProvider, Switch, theme } from 'ant-design-vue';
import { getSystemStatus } from './api/client';

const dark = ref(false);
const selected = ref('overview');
const state = ref<'loading' | 'locked' | 'ready' | 'error'>('loading');
const entries = [
  { id: 'overview', title: '发布概览', path: 'M3 3h7v7H3z M14 3h7v7h-7z M3 14h7v7H3z M14 14h7v7h-7z' },
  { id: 'history', title: '发布历史', path: 'M3 12a9 9 0 1 0 3-6 M3 3v6h6 M12 7v5l3 2' },
  { id: 'audit', title: '审计记录', path: 'M6 3h12v18H6z M9 8h6 M9 12h6 M9 16h4' },
];
const title = computed(() => entries.find((entry) => entry.id === selected.value)?.title ?? '发布概览');
const status = computed(() => ({ loading: '正在连接', locked: '需要登录', ready: '已连接', error: '连接不可用' })[state.value]);

async function refresh() {
  state.value = 'loading';
  state.value = (await getSystemStatus()).kind;
}
onMounted(refresh);
</script>

<template>
  <ConfigProvider :theme="{ algorithm: dark ? theme.darkAlgorithm : theme.defaultAlgorithm, token: { colorPrimary: '#2563eb', borderRadius: 6 } }">
    <div class="workspace" :class="{ dark }">
      <aside class="sidebar" aria-label="主导航">
        <a class="brand" href="/" aria-label="PortKill 发布管理首页">
          <span class="brand-mark" aria-hidden="true"><svg viewBox="0 0 24 24"><path d="M6 3h8a5 5 0 0 1 0 10H6 M6 3v18 M6 8h9 M12 13l6 8" /></svg></span>
          <span>PortKill<small>发布管理</small></span>
        </a>
        <span class="nav-label">工作空间</span>
        <nav>
          <button v-for="entry in entries" :key="entry.id" class="nav-item" :class="{ active: selected === entry.id }" :aria-current="selected === entry.id ? 'page' : undefined" @click="selected = entry.id">
            <svg viewBox="0 0 24 24" aria-hidden="true"><path :d="entry.path" /></svg>{{ entry.title }}
          </button>
        </nav>
        <div class="sidebar-footer"><span class="version-dot"></span>管理端 v0.1.0</div>
      </aside>
      <div class="main-area">
        <header class="topbar">
          <div class="breadcrumb"><span>工作空间</span><span aria-hidden="true">/</span><strong>{{ title }}</strong></div>
          <div class="topbar-actions"><span class="connection-state" role="status"><i :class="state"></i>{{ status }}</span><span class="theme-label">深色</span><Switch v-model:checked="dark" aria-label="切换深色主题" /></div>
        </header>
        <main>
          <div class="page-heading"><div><p class="eyebrow">版本分发</p><h1>{{ title }}</h1><p class="page-description">查看可信版本与发布任务，追踪每一次更新。</p></div><Button :loading="state === 'loading'" @click="refresh">刷新</Button></div>
          <section class="release-panel" aria-labelledby="release-heading">
            <div class="panel-heading"><h2 id="release-heading">{{ selected === 'audit' ? '操作记录' : selected === 'history' ? '版本记录' : '当前发布' }}</h2><span>PortKill · stable · Windows x64</span></div>
            <div class="empty-workspace" :aria-busy="state === 'loading'">
              <div class="empty-symbol" aria-hidden="true"><svg viewBox="0 0 48 48"><path d="M14 21v-7a10 10 0 0 1 20 0v7 M10 21h28v22H10z M24 29v6" /></svg></div>
              <h3>{{ state === 'error' ? '暂时无法连接管理服务' : state === 'loading' ? '正在获取发布状态' : state === 'ready' ? '会话已连接' : '登录后查看发布记录' }}</h3>
              <p>{{ state === 'error' ? '检查服务连接后，刷新重试。' : state === 'locked' ? '使用管理员账号登录，以访问版本、任务和审计信息。' : state === 'loading' ? '请稍候。' : '选择工作空间中的功能查看授权范围内的信息。' }}</p>
            </div>
            <div class="panel-footer"><span>签名验证</span><span>发布确认</span><span>操作审计</span></div>
          </section>
          <p class="workspace-note">发布内容以签名清单为准，客户端由用户确认下载与安装。</p>
        </main>
        <footer class="page-footer"><span>PortKill Update Administration</span><span>版本发布工作空间</span></footer>
      </div>
    </div>
  </ConfigProvider>
</template>
