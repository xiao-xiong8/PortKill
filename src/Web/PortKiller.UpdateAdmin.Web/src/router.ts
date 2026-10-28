import { createRouter, createWebHistory, type RouteRecordRaw } from 'vue-router';
import { useAuth } from './state/auth';
import OverviewPage from './pages/OverviewPage.vue';
import UploadPage from './pages/UploadPage.vue';
import HistoryPage from './pages/HistoryPage.vue';
import JobPage from './pages/JobPage.vue';
import ClientsPage from './pages/ClientsPage.vue';
import SecurityPage from './pages/SecurityPage.vue';
import AuditPage from './pages/AuditPage.vue';
import LoginPage from './pages/LoginPage.vue';

export type RouteMeta = { requiresAuth: boolean; requiresFullSession?: boolean; permission?: string };
const legacyRoutes = ['overview', 'upload', 'history', 'clients', 'security', 'audit'];

function migrateLegacyHash() {
  const hash = window.location.hash.slice(1);
  if (!hash) return;
  const [first, ...rest] = hash.split('/');
  if (!legacyRoutes.includes(first) && first !== 'job') return;
  const path = '/' + [first, ...rest].filter(Boolean).map(segment => decodeURIComponent(segment)).join('/');
  window.history.replaceState(null, '', path);
}

function internalPath(value: unknown): string | undefined {
  if (typeof value !== 'string' || !value.startsWith('/') || value.startsWith('//')) return undefined;
  try {
    const parsed = new URL(value, window.location.origin);
    if (parsed.origin !== window.location.origin) return undefined;
    const path = parsed.pathname + parsed.search + parsed.hash;
    if (/^\/(?:overview|upload|history|clients|security|audit)(?:\/|$)/.test(path) || /^\/job\/[^/]+(?:[?#].*)?$/.test(path)) return path;
  } catch { /* Invalid return targets are ignored. */ }
  return undefined;
}

migrateLegacyHash();
const routes: RouteRecordRaw[] = [
  { path: '/login', name: 'login', component: LoginPage, meta: { requiresAuth: false } satisfies RouteMeta },
  { path: '/overview', name: 'overview', component: OverviewPage, meta: { requiresAuth: true, requiresFullSession: true, permission: 'release.read' } satisfies RouteMeta },
  { path: '/upload/:id?', name: 'upload', component: UploadPage, props: true, meta: { requiresAuth: true, requiresFullSession: true, permission: 'release.upload' } satisfies RouteMeta },
  { path: '/history', name: 'history', component: HistoryPage, meta: { requiresAuth: true, requiresFullSession: true, permission: 'release.read' } satisfies RouteMeta },
  { path: '/job/:id', name: 'job', component: JobPage, props: true, meta: { requiresAuth: true, requiresFullSession: true, permission: 'release.read' } satisfies RouteMeta },
  { path: '/clients', name: 'clients', component: ClientsPage, meta: { requiresAuth: true, requiresFullSession: true, permission: 'release.read' } satisfies RouteMeta },
  { path: '/security', name: 'security', component: SecurityPage, meta: { requiresAuth: true, requiresFullSession: true } satisfies RouteMeta },
  { path: '/audit', name: 'audit', component: AuditPage, meta: { requiresAuth: true, requiresFullSession: true, permission: 'audit.read' } satisfies RouteMeta },
  { path: '/:pathMatch(.*)*', redirect: '/overview' },
];

const router = createRouter({ history: createWebHistory(), routes, scrollBehavior: () => ({ top: 0 }) });
router.beforeEach(async (to) => {
  const auth = useAuth();
  try { await auth.restore(); } catch { /* Unavailable sessions are handled by the route below. */ }
  const full = auth.session.value?.phase === 'full';
  const session = auth.session.value != null;
  const meta = to.meta as RouteMeta;
  if (to.path === '/login') return full ? (internalPath(to.query.returnTo) ?? '/overview') : true;
  if (meta.requiresAuth && !session) return { path: '/login', query: { returnTo: internalPath(to.fullPath) ?? '/overview' } };
  if (meta.requiresFullSession && !full) return { path: '/login', query: { returnTo: internalPath(to.fullPath) ?? '/overview' } };
  if (meta.permission && !auth.can(meta.permission))
    return auth.full.value ? { path: '/security', query: { denied: to.path } } : '/login';
  return true;
});
export { router, internalPath };
