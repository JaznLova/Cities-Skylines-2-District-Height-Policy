declare module "cs2/api" {
  import { MutableRefObject } from 'react';

  export interface ValueBinding<T> {
    readonly value: T;
    subscribe(listener?: BindingListener<T>): ValueSubscription<T>;
    dispose(): void;
  }
  export interface BindingListener<T> {
    (value: T): void;
  }
  export interface Subscription {
    dispose(): void;
  }
  export interface ValueSubscription<T> extends Subscription {
    readonly value: T;
    setChangeListener(listener: BindingListener<T>): void;
  }
  export function bindValue<T>(group: string, name: string, fallbackValue?: T): ValueBinding<T>;
  export function trigger(group: string, name: string, ...args: any[]): void;
  export function useValue<V>(binding: ValueBinding<V>): V;
}
