import { Fragment, type ReactNode } from "react";
import { cx } from "../lib/cx";

export interface DataTableColumn<T> {
  key: string;
  header: string;
  align?: "left" | "right";
  sortable?: boolean;
  render: (row: T) => ReactNode;
}

export interface DataTableProps<T> {
  columns: DataTableColumn<T>[];
  rows: T[];
  rowKey: (row: T) => string;
  sortKey?: string;
  sortDir?: "asc" | "desc";
  onSort?: (key: string) => void;
  selectedKey?: string;
  linkedKeys?: string[];
  onRowClick?: (row: T) => void;
  /** Breakdown/child rows nested under a parent — e.g. a pet under its owner. */
  childRows?: (row: T) => T[] | undefined;
}

/**
 * A sortable, selectable table: sticky header, numeric-column alignment, a
 * hover/selected/linked row tint, and a breakdown-row treatment for anything
 * with children. Fully controlled — no internal fetch or sort state, so it
 * composes with whatever owns the data.
 */
export function DataTable<T>({
  columns,
  rows,
  rowKey,
  sortKey,
  sortDir = "asc",
  onSort,
  selectedKey,
  linkedKeys,
  onRowClick,
  childRows,
}: DataTableProps<T>) {
  const renderRow = (row: T, isChild: boolean) => {
    const key = rowKey(row);
    const isSelected = key === selectedKey;
    const isLinked = linkedKeys?.includes(key) ?? false;
    const kids = !isChild ? childRows?.(row) : undefined;
    return (
      <Fragment key={key}>
        <tr
          className={cx(isChild && "child-row", isSelected && "selected", isLinked && "linked", onRowClick && "linkable")}
          onClick={onRowClick ? () => onRowClick(row) : undefined}
        >
          {columns.map((col) => (
            <td key={col.key} className={col.align === "right" ? "num" : undefined}>
              {col.render(row)}
            </td>
          ))}
        </tr>
        {kids?.map((child) => renderRow(child, true))}
      </Fragment>
    );
  };

  return (
    <div className="table-scroll">
      <table>
        <thead>
          <tr>
            {columns.map((col) => {
              const sorted = sortKey === col.key;
              return (
                <th
                  key={col.key}
                  className={cx(col.align === "right" && "num", col.sortable && "sortable", sorted && "sorted")}
                  onClick={col.sortable && onSort ? () => onSort(col.key) : undefined}
                >
                  {col.header}
                  {col.sortable && (
                    <span className="sort-caret">{sorted ? (sortDir === "asc" ? "▲" : "▼") : ""}</span>
                  )}
                </th>
              );
            })}
          </tr>
        </thead>
        <tbody>{rows.map((row) => renderRow(row, false))}</tbody>
      </table>
    </div>
  );
}
