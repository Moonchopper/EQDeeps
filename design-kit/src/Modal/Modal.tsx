import type { ReactNode } from "react";

export interface ModalProps {
  title: ReactNode;
  children: ReactNode;
  /** Usually a Cancel/Save Button pair, right-aligned. */
  actions?: ReactNode;
}

/**
 * The backdrop + surface every dialog in the kit is built from: a scrim over
 * the page, a raised modal surface, a title, and an action row. No built-in
 * close affordance — most of the app's dialogs close via an explicit action
 * or the backdrop, not a corner ✕, so that choice is left to the caller.
 */
export function Modal({ title, children, actions }: ModalProps) {
  return (
    <div className="modal-backdrop">
      <div className="modal">
        <div className="modal-title">{title}</div>
        {children}
        {actions && <div className="modal-actions">{actions}</div>}
      </div>
    </div>
  );
}
