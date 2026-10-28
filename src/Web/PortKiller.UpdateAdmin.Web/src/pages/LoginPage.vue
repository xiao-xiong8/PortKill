<script setup lang="ts">
import { watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import AuthPanel from '../components/AuthPanel.vue';
import { useAuth } from '../state/auth';
import { internalPath } from '../router';
const auth = useAuth();
const route = useRoute();
const router = useRouter();
watch(() => auth.full.value, full => {
  if (full) void router.replace(internalPath(route.query.returnTo) ?? '/overview');
});
</script>
<template>
  <div class="login-layout"><a class="skip-link" href="#main-content">跳到主内容</a><main id="main-content" tabindex="-1"><AuthPanel /></main><footer class="page-footer"><span>PortKill Update Administration</span><span>安全管理入口</span></footer></div>
</template>
