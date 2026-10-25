<script setup lang="ts">
import { ref } from 'vue';
import { api } from '../api/http';
import type { components } from '../api/contracts.generated';
import { useResource } from '../state/resource';
import JobTable from '../components/JobTable.vue';
import PageControls from '../components/PageControls.vue';
const page = ref(1), kind = ref('');
const { data, error, busy, load } = useResource(signal => api<components['schemas']['PageResponseOfJobListItem']>(`/api/v1/jobs?page=${page.value}&pageSize=20${kind.value ? '&kind=' + kind.value : ''}`, 'GET', undefined, signal));
function change(value: number) { page.value = value; void load(); }
</script>
<template><section class="surface" :aria-busy="busy"><div class="section-heading"><h2>校验与发布记录</h2><div class="actions"><label>任务类型<select v-model="kind" @change="change(1)"><option value="">全部任务</option><option value="publish">发布任务</option><option value="validate">校验任务</option></select></label><button :disabled="busy" @click="load">刷新</button></div></div><p v-if="error" class="notice danger" role="alert">{{ error }}</p><p v-else-if="busy" role="status" class="muted">正在加载记录…</p><JobTable v-else-if="data" :items="data.items" /><PageControls :page="page" :total="data?.total" :busy="busy" @change="change" /></section></template>
