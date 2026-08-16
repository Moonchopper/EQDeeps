export interface SearchInputProps {
  value: string;
  onChange?: (value: string) => void;
  placeholder?: string;
  /** "N shown / total" — the live filter count beside the field. */
  count?: { shown: number; total: number };
}

/** The strip above a table's scroller — a filter field plus a live match count. */
export function SearchInput({ value, onChange, placeholder = "Filter…", count }: SearchInputProps) {
  return (
    <div className="table-search">
      <input
        className="search-input"
        value={value}
        placeholder={placeholder}
        onChange={(e) => onChange?.(e.target.value)}
      />
      {count && (
        <span className="search-count">
          {count.shown} / {count.total}
        </span>
      )}
    </div>
  );
}
