<script setup lang="ts">
defineProps<{ page: number; total: unknown; busy: boolean; pageSize?: number }>();
defineEmits<{ change: [page: number] }>();
</script>
<template><div class="pagination" aria-label="分页"><span>共 {{ total ?? 0 }} 条 · 第 {{ page }} 页</span><button :disabled="busy || page <= 1" @click="$emit('change', page - 1)">上一页</button><button :disabled="busy || page >= 10000 || page * (pageSize ?? 20) >= Number(total ?? 0)" @click="$emit('change', page + 1)">下一页</button></div></template>
