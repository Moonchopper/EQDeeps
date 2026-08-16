// eqdeeps-ui-kit — EQDeeps' dark visual language as a standalone, reusable
// component library: design tokens plus a curated set of presentational
// primitives, extracted as a template for future projects. Prop-driven,
// no app business types (no QuerySpec, no session state) — just the visual
// pattern.
import "./styles/index.css";

export { EqdPage } from "./EqdPage";
export type { EqdPageProps } from "./EqdPage";

export { TokenGallery } from "./tokens/TokenGallery";
export type { TokenGalleryProps } from "./tokens/TokenGallery";
export { SERIES_COLORS, CHART_SERIES_LIMIT, OTHER_COLOR } from "./tokens/colors";

export { Panel } from "./Panel/Panel";
export type { PanelProps } from "./Panel/Panel";

export { Button } from "./Button/Button";
export type { ButtonProps } from "./Button/Button";

export { MiniButton } from "./Chip/MiniButton";
export type { MiniButtonProps } from "./Chip/MiniButton";
export { Tab } from "./Chip/Tab";
export type { TabProps } from "./Chip/Tab";
export { SelectionChip } from "./Chip/SelectionChip";
export type { SelectionChipProps } from "./Chip/SelectionChip";
export { StatusBadge } from "./Chip/StatusBadge";
export type { StatusBadgeProps, StatusBadgeVariant } from "./Chip/StatusBadge";

export { NavRail } from "./NavRail/NavRail";
export type { NavRailProps, NavRailEntry, NavRailGroupData } from "./NavRail/NavRail";
export { NavRailGroup } from "./NavRail/NavRailGroup";
export type { NavRailGroupProps } from "./NavRail/NavRailGroup";
export { NavRailItem } from "./NavRail/NavRailItem";
export type { NavRailItemProps } from "./NavRail/NavRailItem";
export type { RailIcon } from "./NavRail/icons";

export { EmptyState } from "./EmptyState/EmptyState";
export type { EmptyStateProps, EmptyStateVariant } from "./EmptyState/EmptyState";

export { DataTable } from "./Table/DataTable";
export type { DataTableProps, DataTableColumn } from "./Table/DataTable";

export { FormRow } from "./Form/FormRow";
export type { FormRowProps } from "./Form/FormRow";
export { TextInput } from "./Form/TextInput";
export type { TextInputProps } from "./Form/TextInput";
export { SearchInput } from "./Form/SearchInput";
export type { SearchInputProps } from "./Form/SearchInput";
export { Select } from "./Form/Select";
export type { SelectProps, SelectOption } from "./Form/Select";
export { CheckboxRow } from "./Form/CheckboxRow";
export type { CheckboxRowProps } from "./Form/CheckboxRow";
export { RadioRow } from "./Form/RadioRow";
export type { RadioRowProps, RadioOption } from "./Form/RadioRow";

export { Modal } from "./Modal/Modal";
export type { ModalProps } from "./Modal/Modal";
