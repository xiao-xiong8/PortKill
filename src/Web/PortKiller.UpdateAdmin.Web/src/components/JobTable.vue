<script setup lang="ts">
import type { components } from '../api/contracts.generated';
import { openJob, status, time, value } from '../api/display';
defineProps<{ items: components['schemas']['JobListItem'][] }>();
</script>
<template><div v-if="items.length" class="table-scroll"><table><thead><tr><th>版本</th><th>任务</th><th>状态</th><th>序号</th><th>创建时间</th><th>详情</th></tr></thead><tbody><tr v-for="item in items" :key="item.id"><td>{{ item.version ?? '待校验' }}</td><td>{{ item.kind === 'publish' ? '发布' : '校验' }}</td><td><span class="status-label" :class="item.kind === 'validate' ? item.validationState : item.state">{{ status(item.kind === 'validate' ? item.validationState : item.state) }}</span></td><td>{{ value(item.sequence) }}</td><td>{{ time(item.createdUtcMs) }}</td><td><button class="text-button" :aria-label="'查看任务 ' + item.id" @click="openJob(item.id)">查看详情</button></td></tr></tbody></table></div><div v-else class="empty-workspace"><h3>暂无任务记录</h3><p class="muted">上传签名产物后，校验和发布结果会显示在这里。</p></div></template>
