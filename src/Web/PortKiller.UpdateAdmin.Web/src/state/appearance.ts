import type { ComputedRef, InjectionKey, Ref } from 'vue';

export type ThemePreference = 'system' | 'light' | 'dark';
export interface Appearance {
  dark: ComputedRef<boolean>;
  preference: Readonly<Ref<ThemePreference>>;
  setPreference: (value: ThemePreference) => void;
}
export const appearanceKey: InjectionKey<Appearance> = Symbol('portkill.appearance');
