export interface RadioOption {
  value: string;
  label: string;
}

export interface RadioRowProps {
  name: string;
  options: RadioOption[];
  value: string;
  onChange?: (value: string) => void;
  disabled?: boolean;
}

/** A small set of mutually exclusive options laid out in a row rather than stacked. */
export function RadioRow({ name, options, value, onChange, disabled }: RadioRowProps) {
  return (
    <div className="radio-row">
      {options.map((o) => (
        <label key={o.value}>
          <input
            type="radio"
            name={name}
            value={o.value}
            checked={value === o.value}
            disabled={disabled}
            onChange={() => onChange?.(o.value)}
          />
          {o.label}
        </label>
      ))}
    </div>
  );
}
