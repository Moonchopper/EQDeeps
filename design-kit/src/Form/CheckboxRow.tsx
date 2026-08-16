export interface CheckboxRowProps {
  label: string;
  checked: boolean;
  onChange?: (checked: boolean) => void;
  disabled?: boolean;
}

/** A single checkbox with its label beside it, aligned like a radio row. */
export function CheckboxRow({ label, checked, onChange, disabled }: CheckboxRowProps) {
  return (
    <label className="inline-check">
      <input
        type="checkbox"
        checked={checked}
        disabled={disabled}
        onChange={(e) => onChange?.(e.target.checked)}
      />
      {label}
    </label>
  );
}
